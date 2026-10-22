using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace Bezoro.AhkManager.Presentation;

public sealed class BrushTaskHelper : IBrushDispatcher, IBrushHelperQuiescence
{
    public bool Fixture => false;
    int dispatching;
    public static string Profile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManager");
    public static bool Elevated()
    {
        using var process = Process.GetCurrentProcess(); using var held = BrushHeld.Open(process.Id);
        return held?.Elevation == true;
    }
    public static bool TryArguments(string[] args, out string endpoint, out Guid nonce, out BrushOperation operation)
    {
        endpoint = ""; nonce = default; operation = default;
        if (args.Length != 4 || args[0] != "--brush-helper" || args[1].Length != 45 || !args[1].StartsWith("Bezoro.Brush.", StringComparison.Ordinal) || !Guid.TryParseExact(args[1][13..], "N", out _) || !Guid.TryParseExact(args[2], "N", out nonce) || nonce == Guid.Empty || !int.TryParse(args[3], out int op) || !Enum.IsDefined(typeof(BrushOperation), op)) return false;
        endpoint = args[1]; operation = (BrushOperation)op; return true;
    }
    public async Task<BrushReply> Dispatch(BrushJournal j, Func<string, Result> authorize, CancellationToken token)
    {
        if (j.Fixture || Elevated() || j.Sid != BrushTaskControls.Sid || !AuthorityFiles(j)) return new(false, false, false, false, "Helper real recusa fixture, GUI elevada ou vinculo invalido.");
        Interlocked.Increment(ref dispatching);
        try
        {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        string endpoint = "Bezoro.Brush." + Guid.NewGuid().ToString("N");
        using var server = PrivateServer(endpoint, j.Sid);
        // A returned ShellExecute process is mandatory, never a discovered PID fallback.
        Process? returned = null;
        try
        {
            returned = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(j.Executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(j.Executable)! };
                start.ArgumentList.Add("--brush-helper"); start.ArgumentList.Add(endpoint); start.ArgumentList.Add(j.Nonce.ToString("N")); start.ArgumentList.Add(((int)j.Operation).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Process.Start(start);
            });
            if (returned is null) throw new IOException("Windows nao retornou identidade do helper.");
            using var helper = BrushHeld.Open(returned.Id);
            if (helper is null || helper.Elevation != true || !helper.Matches(j, false) || !Native.Identity(returned.SafeHandle, out long created, out string image) || created != helper.Created || !Paths.Equal(image, j.Executable)) throw new IOException("Identidade retornada do helper nao confirmada.");
            await server.WaitForConnectionAsync(deadline.Token);
            using var peer = NativeExistingPeer.Authenticate(server, true, Scope(j));
            if (peer is null || peer.Pid != helper.Pid || peer.Created != helper.Created || !helper.Alive) throw new IOException("Peer helper invalido.");
            var hello = await Frame.Read(server, deadline.Token); if (!hello.Bound(j.Nonce) || hello.Kind != 1) throw new IOException("Handshake invalido.");
            await new Frame(2, j.Nonce).Write(server, deadline.Token);
            while (true)
            {
                var frame = await Frame.Read(server, deadline.Token);
                if (!frame.Bound(j.Nonce) || !peer.Alive || !helper.Alive || !AuthorityFiles(j)) throw new IOException("Helper ou autoridade mudou.");
                if (frame.Kind == 20)
                {
                    var reply = JsonSerializer.Deserialize(frame.Payload, BrushJson.Default.BrushReply) ?? throw new IOException("Resposta invalida.");
                    if (reply.Message.Length > 256) throw new IOException("Resposta fora do limite.");
                    await new Frame(21, j.Nonce).Write(server, deadline.Token);
                    return reply;
                }
                string stage = frame.Kind switch { 10 => "Run", 11 => "Stop", 12 => "Register", _ => "" };
                var ready = stage.Length > 0 ? authorize(stage) : Result.Fail("Etapa invalida.");
                if (!ready.Success) { await new Frame(4, j.Nonce).Write(server, deadline.Token); return new(false, false, false, false, ready.Message); }
                if (!helper.Alive || !peer.Alive || !AuthorityFiles(j)) throw new IOException("Vinculo mudou depois do journal.");
                await new Frame(3, j.Nonce).Write(server, deadline.Token);
            }
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return new(false, false, false, true, "UAC cancelado antes de qualquer efeito Brush."); }
        finally { returned?.Dispose(); }
        }
        finally { Interlocked.Decrement(ref dispatching); }
    }
    public Result CheckQuiescence(BrushJournal j) => ProveQuiescence(j, Volatile.Read(ref dispatching) != 0, Environment.ProcessId, () => Candidates(j));
    static IEnumerable<IBrushHelperCandidate?> Candidates(BrushJournal j)
    {
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(j.Executable));
        try { foreach (var candidate in candidates) yield return BrushHeld.Open(candidate.Id); }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }
    public static Result ProveQuiescence(BrushJournal j, bool dispatchActive, int currentPid, Func<IEnumerable<IBrushHelperCandidate?>> candidates)
    {
        if (dispatchActive) return Result.Fail("Dispatch Brush em andamento.");
        var retained = new List<IBrushHelperCandidate>(); bool currentKnown = false;
        try
        {
            foreach (var candidate in candidates())
            {
                if (candidate is null) return Result.Fail("Candidato Manager inacessivel ou nao classificado.");
                retained.Add(candidate);
                if (!candidate.Alive) return Result.Fail("Identidade Manager mudou durante consulta.");
                if (!Paths.Equal(candidate.Image,j.Executable) || candidate.Sid != j.Sid || candidate.Session != j.Session) continue;
                if (candidate.Elevation != false || candidate.Pid != (uint)currentPid || candidate.Login != j.LoginKey)
                    return Result.Fail("Helper vivo ou Manager nao classificado impede conclusao de ausencia.");
                currentKnown = true;
            }
            return currentKnown && retained.All(candidate => candidate.Alive) ? Result.Ok("GUI normal conhecida; nenhum helper relevante vivo.") : Result.Fail("GUI ou quiescencia nao confirmada.");
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return Result.Fail("Quiescencia nao confirmada."); }
        finally { foreach (var candidate in retained) candidate.Dispose(); }
    }
    public static int Run(string[] args)
    {
        if (!TryArguments(args, out var endpoint, out var nonce, out var op)) return 2;
        try { return RunOnce(endpoint, nonce, op).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or Win32Exception or JsonException or COMException) { return 1; }
    }
    static async Task<int> RunOnce(string endpoint, Guid nonce, BrushOperation op)
    {
        if (!Elevated()) return 1;
        var j = BrushOperationStore.Read(Path.Combine(Profile, "brush-operation.json"));
        if (j.Fixture || j.Completed || j.Nonce != nonce || j.Operation != op || j.Sid != BrushTaskControls.Sid || j.DeadlineUtcTicks <= DateTime.UtcNow.Ticks || j.DeadlineUtcTicks > DateTime.UtcNow.AddMinutes(2).Ticks || !AuthorityFiles(j)) return 1;
        using var self = BrushHeld.Open(Environment.ProcessId); if (self is null || !self.Matches(j, false) || self.Elevation != true) return 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(timeout.Token);
        using var peer = NativeExistingPeer.Authenticate(pipe, false, Scope(j));
        using var manager = peer is null ? null : BrushHeld.Open((int)peer.Pid);
        if (peer is null || manager is null || peer.Pid != j.ManagerPid || peer.Created != j.ManagerCreated || manager.Created != peer.Created || manager.Elevation != false || !manager.Matches(j, true)) return 1;
        await new Frame(1, nonce).Write(pipe, timeout.Token); var hello = await Frame.Read(pipe, timeout.Token); if (hello.Kind != 2 || !hello.Bound(nonce)) return 1;
        var consumed = new HashSet<string>();
        bool Alive() => Connected(pipe) && manager.Alive && peer.Alive && AuthorityFiles(j) && DateTime.UtcNow.Ticks < j.DeadlineUtcTicks;
        async Task<Result> Step(string stage)
        {
            if (!Alive() || !BrushTaskControls.Allowed(op, consumed, stage) || !consumed.Add(stage)) return Result.Fail("Solicitante ou etapa invalida.");
            byte kind = stage switch { "Run" => 10, "Stop" => 11, "Register" => 12, _ => 0 };
            await new Frame(kind, nonce).Write(pipe, timeout.Token); var ack = await Frame.Read(pipe, timeout.Token);
            if (!ack.Bound(nonce) || ack.Kind != 3 || !Alive()) return Result.Fail("Etapa nao autorizada.");
            var current = BrushOperationStore.Read(Path.Combine(Profile, "brush-operation.json"));
            return current.Nonce == nonce && current.Stage == stage && !current.Completed && BindingsEqual(j, current) ? Result.Ok("Etapa duravel confirmada.") : Result.Fail("Intencao duravel divergente.");
        }
        var reply = await BrushTaskControls.RunHelper(j, new WindowsBrushTask(() => { timeout.Token.ThrowIfCancellationRequested(); var p = new WindowsBrushTask().Probe(); return Alive() && p.Ready && p.Definition == j.Definition && p.Security == j.Security && p.LogonEnabled == j.AllowedLogon ? Result.Ok("Solicitante, autoridade e tarefa ainda verificados.") : Result.Fail("Solicitante ou tarefa mudou antes do efeito."); }), new WindowsProcesses(), Step, Alive, timeout.Token);
        string payload = JsonSerializer.Serialize(reply, BrushJson.Default.BrushReply);
        await new Frame(20, nonce, payload).Write(pipe, timeout.Token);
        var receipt = await Frame.Read(pipe, timeout.Token); return receipt.Kind == 21 && receipt.Bound(nonce) ? 0 : 1;
    }
    static bool BindingsEqual(BrushJournal a, BrushJournal b) => a.Fixture == b.Fixture && a.Operation == b.Operation && a.RequestedLogon == b.RequestedLogon && a.ManagerPid == b.ManagerPid && a.ManagerCreated == b.ManagerCreated && a.Sid == b.Sid && a.Session == b.Session && a.LoginKey == b.LoginKey && a.CatalogHash == b.CatalogHash && a.LedgerHash == b.LedgerHash && a.AnchorHash == b.AnchorHash && a.AuthorityId == b.AuthorityId && a.AssemblyHash == b.AssemblyHash && a.AllowedLogon == b.AllowedLogon && a.ExecutableHash == b.ExecutableHash && a.Executable == b.Executable && a.Definition == b.Definition && a.Security == b.Security && a.DeadlineUtcTicks == b.DeadlineUtcTicks;
    static ExistingScope Scope(BrushJournal j) => new(j.Sid, j.Session, j.CatalogHash, "", j.Executable);
    public static bool AuthorityFiles(BrushJournal j)
    {
        if (j.Fixture) return false;
        try
        {
            NormalMigrationEffects.GuardProfileFiles(Profile);
            var catalog = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Profile,"catalog.json")), AppJson.Default.Catalog);
            var ledger = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Profile,"launch-requests.json")), LedgerJson.Default.LedgerDocument);
            var anchor = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Profile,"launch-authority.anchor.json")), LedgerJson.Default.AuthorityBinding);
            if (catalog?.Schema != 5 || catalog.Authority?.LedgerId != j.AuthorityId || catalog.Authority.Fixture || catalog.Authority.Sid != j.Sid || catalog.Scripts.Count(BrushTaskControls.Matches) != 1 || ledger?.Schema != 2 || ledger.Requests.Count != 0 || !AuthorityBinding.Same(catalog.Authority, ledger.Binding) || !AuthorityBinding.Same(anchor, ledger.Binding)) return false;
            return NormalMigrationEffects.FileHash(Path.Combine(Profile,"catalog.json")) == j.CatalogHash && NormalMigrationEffects.FileHash(Path.Combine(Profile,"launch-requests.json")) == j.LedgerHash && NormalMigrationEffects.FileHash(Path.Combine(Profile,"launch-authority.anchor.json")) == j.AnchorHash && NormalMigrationEffects.FileHash(j.Executable) == j.ExecutableHash && NormalMigrationEffects.FileHash(Path.ChangeExtension(j.Executable, ".dll")) == j.AssemblyHash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException) { return false; }
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool PeekNamedPipe(SafePipeHandle pipe, nint data, uint size, nint read, nint available, nint left);
    static bool Connected(PipeStream pipe) => pipe.IsConnected && PeekNamedPipe(pipe.SafePipeHandle,0,0,0,0,0);
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes security);
    public static NamedPipeServerStream PrivateServer(string endpoint, string sid)
    {
        if (!endpoint.StartsWith("Bezoro.Brush.", StringComparison.Ordinal) || endpoint.Length != 45 || !Guid.TryParseExact(endpoint[13..], "N", out _) || new SecurityIdentifier(sid).Value != sid) throw new ArgumentException("Endpoint privado invalido.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;" + sid + ")",1,out nint descriptor,out _)) throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + endpoint, 0x3 | 0x40000000 | 0x80000, 0x8, 1, 512, 512, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); } catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    public sealed record Frame(byte Kind, Guid Nonce, string Payload = "")
    {
        public bool Bound(Guid nonce) => Nonce == nonce && nonce != Guid.Empty;
        public async Task Write(Stream stream, CancellationToken token)
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(Payload); if (payload.Length > 490) throw new IOException("Frame fora do limite.");
            byte[] data = new byte[4 + 17 + payload.Length]; BitConverter.GetBytes(17 + payload.Length).CopyTo(data,0); data[4]=Kind; Nonce.TryWriteBytes(data.AsSpan(5,16)); payload.CopyTo(data,21);
            await stream.WriteAsync(data, token); await stream.FlushAsync(token);
        }
        public static async Task<Frame> Read(Stream stream, CancellationToken token)
        {
            var head = new byte[4]; await stream.ReadExactlyAsync(head, token); int size = BitConverter.ToInt32(head); if (size is < 17 or > 507) throw new IOException("Frame fora do limite.");
            var body = new byte[size]; await stream.ReadExactlyAsync(body,token); return new(body[0],new Guid(body.AsSpan(1,16)),new System.Text.UTF8Encoding(false,true).GetString(body,17,size-17));
        }
    }
}

public interface IBrushHelperCandidate : IDisposable
{
    uint Pid { get; } string Image { get; } string Sid { get; } int Session { get; } string Login { get; } bool? Elevation { get; } bool Alive { get; }
}
// Query-only handles pin token, session, login and creation throughout each request.
internal sealed class BrushHeld : IDisposable, IBrushHelperCandidate
{
    readonly SafeProcessHandle handle;
    public uint Pid { get; }
    public long Created { get; }
    public string Image { get; }
    public string Sid { get; }
    public int Session { get; }
    public string Login { get; }
    public bool? Elevation { get; }
    public bool Alive => Native.WaitForSingleObject(handle,0) == 258 && Native.Identity(handle,out long c,out string image) && c == Created && Paths.Equal(image,Image);
    BrushHeld(SafeProcessHandle handle, uint pid, long created, string image, string sid, int session, string login, bool? elevated) { this.handle=handle; Pid=pid; Created=created; Image=image; Sid=sid; Session=session; Login=login; Elevation=elevated; }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct Statistics { public Luid Token, Authentication; public long Expiration; public int Type, Level; public uint Charged, Available, Groups, Privileges; public Luid Modified; }
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,out int value,int length,out int returned);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,out Statistics value,int length,out int returned);
    public static BrushHeld? Open(int pid)
    {
        var handle = Native.OpenProcess(0x1000 | 0x100000,false,pid); bool kept=false;
        try
        {
            if (handle.IsInvalid || Native.WaitForSingleObject(handle,0) != 258 || !Native.Identity(handle,out var created,out var image) || !OpenProcessToken(handle,8,out var token)) return null;
            using (token)
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle()); string sid=identity.User?.Value ?? "";
                if (sid.Length==0 || !GetTokenInformation(token,12,out int session,4,out int returned) || returned!=4 || !GetTokenInformation(token,10,out Statistics statistics,Marshal.SizeOf<Statistics>(),out returned) || returned != Marshal.SizeOf<Statistics>()) return null;
                bool? elevation=GetTokenInformation(token,20,out int elevated,4,out returned) && returned==4 ? elevated!=0 : null;
                var result = new BrushHeld(handle,(uint)pid,created,image,sid,session,sid+"|"+statistics.Authentication.High.ToString("X8")+statistics.Authentication.Low.ToString("X8"),elevation);
                if (!result.Alive) return null; kept=true; return result;
            }
        }
        finally { if(!kept) handle.Dispose(); }
    }
    public bool Matches(BrushJournal j,bool manager) => Alive && Sid==j.Sid && Session==j.Session && Paths.Equal(Image,j.Executable) && (!manager || Pid==j.ManagerPid && Created==j.ManagerCreated && Login==j.LoginKey);
    public void Dispose() => handle.Dispose();
}
