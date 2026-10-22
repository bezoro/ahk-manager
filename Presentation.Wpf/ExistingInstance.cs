using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Presentation;

public enum ExistingOperation : byte { ShowExisting = 1, Dedupe = 2 }
public enum ExistingStatus : byte { Shown = 1, ShownWithoutForeground, Deduplicated, NotReady, Closing, Rejected, TimedOut }
public enum ExistingLifecycle { Starting, Ready, Closing }
public sealed record ExistingReply(ExistingStatus Status, bool ForegroundGranted = false)
{
    public bool Success => Status is ExistingStatus.Shown or ExistingStatus.ShownWithoutForeground or ExistingStatus.Deduplicated;
}
public sealed record ExistingScope(string Sid, int Session, string CatalogDigest, string Endpoint, string Executable)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetLongPathName(string path, StringBuilder output, uint capacity);
    public static void ValidateDirectorySpelling(string directory)
    {
        if (directory.Split(new[] { '\\', '/' }).Any(part => part is not ("." or "..") && (part.EndsWith('.') || part.EndsWith(' ')))) throw new ArgumentException("Diretorio ambiguo recusado.");
    }
    public static string CanonicalDirectory(string directory)
    {
        ValidateDirectorySpelling(directory);
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.Contains('"')) throw new ArgumentException("Diretorio local absoluto necessario.");
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length < 3 || full[2..].Contains(':') || full.Any(c => c < 32 || "<>|*?".Contains(c)) || full[3..].Split(Path.DirectorySeparatorChar).Any(part => part.EndsWith('.') || part.EndsWith(' '))) throw new ArgumentException("Diretorio ambiguo recusado.");
        string existing = full; var missing = new Stack<string>();
        while (!Directory.Exists(existing))
        {
            if (File.Exists(existing)) throw new ArgumentException("Diretorio esperado.");
            missing.Push(Path.GetFileName(existing)); existing = Path.GetDirectoryName(existing) ?? throw new ArgumentException("Raiz invalida.");
        }
        for (string? current = existing; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Diretorio com redirecionamento recusado.");
        var buffer = new StringBuilder(32768); if (GetLongPathName(existing, buffer, (uint)buffer.Capacity) == 0) throw new IOException("Identidade do diretorio indisponivel.");
        existing = buffer.ToString(); while (missing.TryPop(out var name)) existing = Path.Combine(existing, name);
        return existing.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    }
    public static ExistingScope Current(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        string sid = identity.User?.Value ?? throw new IOException("SID indisponivel.");
        using var process = Process.GetCurrentProcess(); int session = process.SessionId;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDirectory(directory))));
        string endpoint = "Bezoro.AhkManager.Reopen.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "|" + session + "|" + digest)));
        return new(sid, session, digest, endpoint, Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("Executavel indisponivel.")));
    }
}

// A frame carries no argv, path, HWND, script action or effect authority.
public sealed record ExistingFrame(byte Kind, Guid Nonce, string CatalogDigest, long Deadline = 0)
{
    public const byte Challenge = 10, Response = 20, Receipt = 30;
    public const int BodySize = 58, MaxFrame = 512;
    public async Task Write(Stream stream, CancellationToken token)
    {
        var bytes = new byte[4 + BodySize]; BitConverter.TryWriteBytes(bytes.AsSpan(0,4), BodySize);
        bytes[4] = 1; bytes[5] = Kind; Nonce.TryWriteBytes(bytes.AsSpan(6,16));
        Convert.FromHexString(CatalogDigest).CopyTo(bytes,22); BitConverter.TryWriteBytes(bytes.AsSpan(54,8), Deadline);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<ExistingFrame> Read(Stream stream, CancellationToken token)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, token); int length = BitConverter.ToInt32(size);
        if (length > MaxFrame || length != BodySize) throw new InvalidDataException("Tamanho de frame recusado.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        if (bytes[0] != 1) throw new InvalidDataException("Protocolo recusado.");
        return new(bytes[1], new Guid(bytes.AsSpan(2,16)), Convert.ToHexString(bytes.AsSpan(18,32)), BitConverter.ToInt64(bytes,50));
    }
    public bool BoundTo(ExistingScope scope, Guid nonce) => Nonce == nonce && nonce != Guid.Empty && CatalogDigest == scope.CatalogDigest;
}

public interface IExistingPeer : IDisposable { uint Pid { get; } long Created { get; } bool Alive { get; } }
public delegate IExistingPeer? ExistingPeerVerifier(PipeStream pipe, bool server, ExistingScope scope);
public sealed class NativeExistingPeer : IExistingPeer
{
    readonly SafeProcessHandle process;
    public uint Pid { get; }
    public long Created { get; }
    public bool Alive => !process.IsClosed && WaitForSingleObject(process, 0) == 258 && GetProcessId(process) == Pid && GetProcessTimes(process, out var created, out _, out _, out _) && created == Created;
    NativeExistingPeer(SafeProcessHandle process, uint pid, long created) { this.process = process; Pid = pid; Created = created; }
    [StructLayout(LayoutKind.Sequential)] struct TokenUser { public nint Sid; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint data, int length, out int returned);
    public static IExistingPeer? Authenticate(PipeStream pipe, bool server, ExistingScope scope)
    {
        bool got = server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!got || pid == 0) return null;
        var handle = OpenProcess(0x1000 | 0x100000, false, pid);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        bool transferred = false;
        try
        {
            if (WaitForSingleObject(handle,0) != 258 || GetProcessId(handle) != pid || !GetProcessTimes(handle,out long created,out _,out _,out _) || created <= 0) return null;
            var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
            if (!QueryFullProcessImageName(handle,0,image,ref length) || !Paths.Equal(image.ToString(),scope.Executable) || !OpenProcessToken(handle,8,out var token)) return null;
            using (token)
            {
                _ = GetTokenInformation(token,1,0,0,out int needed); if (needed <= 0 || needed > 32768) return null;
                nint data = Marshal.AllocHGlobal(Math.Max(needed,4));
                try
                {
                    if (!GetTokenInformation(token,1,data,needed,out int returned) || returned > needed || returned < Marshal.SizeOf<TokenUser>()) return null;
                    var user = Marshal.PtrToStructure<TokenUser>(data); string sid = new SecurityIdentifier(user.Sid).Value;
                    if (sid != scope.Sid || !GetTokenInformation(token,12,data,4,out returned) || returned != 4 || Marshal.ReadInt32(data) != scope.Session) return null;
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            var peer = new NativeExistingPeer(handle,pid,created); if (!peer.Alive) return null;
            transferred = true; return peer;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or UnauthorizedAccessException) { return null; }
        finally { if (!transferred) handle.Dispose(); }
    }
    public void Dispose() => process.Dispose();
}

public sealed class ExistingInstanceServer : IAsyncDisposable
{
    readonly ExistingScope scope;
    readonly ExistingPeerVerifier authenticate;
    readonly TimeSpan requestTimeout;
    readonly CancellationTokenSource closing = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object sync = new();
    NamedPipeServerStream? pipe;
    Func<CancellationToken,Task<ExistingStatus>>? show;
    int lifecycle;
    public ExistingLifecycle Lifecycle => (ExistingLifecycle)Volatile.Read(ref lifecycle);
    public Task Completion { get; }
    public ExistingInstanceServer(ExistingScope scope, ExistingPeerVerifier? authenticate = null, TimeSpan? requestTimeout = null)
    {
        this.scope = scope; this.authenticate = authenticate ?? NativeExistingPeer.Authenticate;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        // FirstPipeInstance bind is synchronous; callers already hold owner AND catalog lock.
        pipe = NewPipe(); Completion = Serve();
    }
    NamedPipeServerStream NewPipe() => new(scope.Endpoint,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,512,512);
    public void MarkReady(Func<CancellationToken,Task<ExistingStatus>> showExisting)
    {
        lock (sync) { if (Lifecycle == ExistingLifecycle.Closing) return; show = showExisting; Volatile.Write(ref lifecycle,(int)ExistingLifecycle.Ready); ready.TrySetResult(); }
    }
    public void BeginClosing()
    {
        lock (sync) { if (Lifecycle == ExistingLifecycle.Closing) return; Volatile.Write(ref lifecycle,(int)ExistingLifecycle.Closing); closing.Cancel(); pipe?.Dispose(); }
    }
    async Task Serve()
    {
        while (!closing.IsCancellationRequested)
        {
            NamedPipeServerStream? connection; lock(sync) connection = pipe;
            if (connection is null) return;
            bool connected = false;
            try
            {
                await connection.WaitForConnectionAsync(closing.Token); connected = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closing.Token); deadline.CancelAfter(requestTimeout);
                using var peer = authenticate(connection,true,scope);
                if (peer is null || !peer.Alive) continue;
                Guid nonce = Guid.NewGuid(); await new ExistingFrame(ExistingFrame.Challenge,nonce,scope.CatalogDigest).Write(connection,deadline.Token);
                var request = await ExistingFrame.Read(connection,deadline.Token);
                long now = DateTime.UtcNow.Ticks;
                if (!request.BoundTo(scope,nonce) || request.Kind is not (1 or 2) || request.Deadline <= now || request.Deadline > now + TimeSpan.FromSeconds(10).Ticks) continue;
                deadline.CancelAfter(TimeSpan.FromTicks(Math.Min(request.Deadline - now,requestTimeout.Ticks)));
                using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                int responseSent = 0;
                var receipt = WatchReceipt(connection,disconnect,scope,nonce,() => Volatile.Read(ref responseSent) != 0);
                ExistingStatus status;
                try
                {
                    if (!peer.Alive) status = ExistingStatus.Rejected;
                    else if (Lifecycle == ExistingLifecycle.Closing) status = ExistingStatus.Closing;
                    else if (request.Kind == (byte)ExistingOperation.Dedupe) status = ExistingStatus.Deduplicated;
                    else
                    {
                        await ready.Task.WaitAsync(disconnect.Token);
                        disconnect.Token.ThrowIfCancellationRequested();
                        if (!peer.Alive) status = ExistingStatus.Rejected;
                        else if (Lifecycle != ExistingLifecycle.Ready || show is null) status = ExistingStatus.Closing;
                        else status = await show(disconnect.Token).WaitAsync(disconnect.Token);
                    }
                    disconnect.Token.ThrowIfCancellationRequested();
                    Volatile.Write(ref responseSent,1);
                    await new ExistingFrame((byte)(ExistingFrame.Response + (byte)status),nonce,scope.CatalogDigest).Write(connection,deadline.Token);
                    _ = await receipt.WaitAsync(deadline.Token);
                }
                finally { disconnect.Cancel(); await receipt; }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException or InvalidDataException) { }
            finally
            {
                lock (sync)
                {
                    if (closing.IsCancellationRequested) { connection.Dispose(); pipe = null; }
                    else if (connected || connection.IsConnected) connection.Disconnect();
                }
            }
        }
    }
    static async Task<bool> WatchReceipt(Stream connection, CancellationTokenSource cancel, ExistingScope scope, Guid nonce, Func<bool> responseSent)
    {
        try
        {
            var receipt = await ExistingFrame.Read(connection,cancel.Token);
            if (receipt.Kind == ExistingFrame.Receipt && receipt.Deadline == 0 && receipt.BoundTo(scope,nonce) && responseSent()) return true;
            cancel.Cancel(); return false;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { cancel.Cancel(); return false; }
    }
    public async ValueTask DisposeAsync()
    {
        BeginClosing(); await Completion.WaitAsync(TimeSpan.FromSeconds(2)); closing.Dispose();
    }
}

public static class ExistingInstanceClient
{
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint processId);
    public static async Task<ExistingReply> Request(ExistingScope scope, ExistingOperation operation, TimeSpan? timeout = null, ExistingPeerVerifier? authenticate = null, Func<uint,bool>? grantForeground = null)
    {
        if (operation is not (ExistingOperation.ShowExisting or ExistingOperation.Dedupe)) return new(ExistingStatus.Rejected);
        var duration = timeout ?? TimeSpan.FromSeconds(10); if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(duration); long until = DateTime.UtcNow.Add(duration).Ticks;
        using var pipe = new NamedPipeClientStream(".",scope.Endpoint,PipeDirection.InOut,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool granted = false;
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            using var peer = (authenticate ?? NativeExistingPeer.Authenticate)(pipe,false,scope);
            if (peer is null || !peer.Alive) return new(ExistingStatus.Rejected);
            var hello = await ExistingFrame.Read(pipe,deadline.Token);
            if (hello.Kind != ExistingFrame.Challenge || hello.Deadline != 0 || !hello.BoundTo(scope,hello.Nonce) || !peer.Alive) return new(ExistingStatus.Rejected);
            if (operation == ExistingOperation.ShowExisting) { if (!peer.Alive) return new(ExistingStatus.Rejected); granted = (grantForeground ?? AllowSetForegroundWindow)(peer.Pid); }
            await new ExistingFrame((byte)operation,hello.Nonce,scope.CatalogDigest,until).Write(pipe,deadline.Token);
            var reply = await ExistingFrame.Read(pipe,deadline.Token);
            if (!reply.BoundTo(scope,hello.Nonce) || reply.Deadline != 0 || !peer.Alive || reply.Kind < 21 || reply.Kind > 27) return new(ExistingStatus.Rejected,granted);
            var status = (ExistingStatus)(reply.Kind - ExistingFrame.Response);
            if (operation == ExistingOperation.Dedupe && status is ExistingStatus.Shown or ExistingStatus.ShownWithoutForeground || operation == ExistingOperation.ShowExisting && status == ExistingStatus.Deduplicated) return new(ExistingStatus.Rejected,granted);
            await new ExistingFrame(ExistingFrame.Receipt,hello.Nonce,scope.CatalogDigest).Write(pipe,deadline.Token);
            return new(status,granted);
        }
        catch (OperationCanceledException) { return new(ExistingStatus.TimedOut,granted); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return new(ExistingStatus.Rejected,granted); }
    }
}

public interface IExistingWindow
{
    bool Closing { get; }
    bool Minimized { get; }
    void Restore();
    void Show();
    bool Activate();
    void Flash();
}
public static class ExistingWindowDisplay
{
    public static ExistingStatus Show(IExistingWindow window, CancellationToken token)
    {
        if (token.IsCancellationRequested || window.Closing) return ExistingStatus.Closing;
        if (window.Minimized) window.Restore();
        window.Show();
        if (window.Activate()) return ExistingStatus.Shown;
        window.Flash(); return ExistingStatus.ShownWithoutForeground;
    }
}
public sealed class WpfExistingWindow(MainWindow window) : IExistingWindow
{
    public bool Closing => window.Session.ShuttingDown || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished;
    public bool Minimized => window.WindowState == WindowState.Minimized;
    public void Restore() => window.WindowState = WindowState.Normal;
    public void Show() => window.Show();
    public bool Activate() => window.Activate();
    [StructLayout(LayoutKind.Sequential)] struct FlashInfo { public uint Size; public nint Window; public uint Flags,Count,Timeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo information);
    public void Flash() { var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = new WindowInteropHelper(window).Handle, Flags = 2 | 12, Count = 3 }; _ = FlashWindowEx(ref info); }
}
