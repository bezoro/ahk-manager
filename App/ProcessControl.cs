using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Bezoro.AhkManager.Tests")]

namespace Bezoro.AhkManager;

public interface IProcessAccess
{
    Task<Snapshot> Snapshot(CancellationToken token);
    Result Preflight(ScriptEntry entry);
    Task<Result> Start(ScriptEntry entry, CancellationToken token);
    Task<Result> Stop(ProcessIdentity identity, CancellationToken token);
    Task<Result> Prove(ProcessIdentity identity, CancellationToken token) => Task.FromResult(Result.Fail("Prova positiva de processo indisponível."));
}
public interface IRecycler { Result Recycle(string path); }

public sealed class ActionController(IProcessAccess processes, IRecycler recycler, Func<ScriptEntry, ScriptAction, Result>? effectAuthority = null)
{
    readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ElevationOutcome> ExecuteElevation(ScriptEntry entry, ManualLaunchPermit permit, IManualElevationDispatcher dispatcher, CancellationToken token)
    {
        try { if (!await gate.WaitAsync(0, token)) return new(ElevationDisposition.RefusedBeforeDispatch, "Outra ação está em andamento."); }
        catch (OperationCanceledException) { return new(ElevationDisposition.Cancelled, "Cancelado antes da ação."); }
        try
        {
            token.ThrowIfCancellationRequested();
            entry = entry.Copy(); entry.Path = Paths.Normalize(entry.Path);
            var valid = permit.Validate(entry, dispatcher.Fixture); if (!valid.Success) return new(ElevationDisposition.RefusedBeforeDispatch, valid.Message);
            if (entry.ManagementMode is ManagementMode.ExternalPending or ManagementMode.Retired) return new(ElevationDisposition.RefusedBeforeDispatch, "Fonte externa ou supervisor pendente; UAC não altera esse contrato.");
            var snapshot = await processes.Snapshot(token);
            var policy = Policy.Check(entry, snapshot, ScriptAction.Start, File.Exists(entry.Path)); if (!policy.Success) return new(ElevationDisposition.RefusedBeforeDispatch, policy.Message);
            var ready = processes.Preflight(entry); if (!ready.Success) return new(ElevationDisposition.RefusedBeforeDispatch, ready.Message);
            token.ThrowIfCancellationRequested();
            return await dispatcher.Dispatch(permit, entry, token);
        }
        catch (OperationCanceledException) { return new(permit.DispatchStarted ? ElevationDisposition.Uncertain : ElevationDisposition.Cancelled, permit.DispatchStarted ? "Pedido entregue; cancelamento do gerenciador não desfaz o Windows." : "Cancelado antes de entregar a solicitação ao Windows."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        { return new(permit.DispatchStarted ? ElevationDisposition.Uncertain : ElevationDisposition.RefusedBeforeDispatch, "Resultado não confirmado: " + ex.Message); }
        finally { gate.Release(); }
    }
    public async Task<Result> Execute(ScriptEntry entry, ScriptAction action, bool confirmed, CancellationToken token = default)
    {
        try { if (!await gate.WaitAsync(0, token)) return Result.Fail("Outra ação está em andamento. Aguarde e atualize."); }
        catch (OperationCanceledException) { return Result.Fail("Cancelado antes da ação."); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (action is ScriptAction.Start or ScriptAction.Restart)
            {
                if (processes is WindowsProcesses && effectAuthority is null) return Result.Fail("Autoridade de início não fornecida.");
                var authority = effectAuthority?.Invoke(entry, action); if (authority is { Success: false }) return authority;
            }
            if (entry.RunAsAdministrator && action is ScriptAction.Start or ScriptAction.Restart) return Result.Fail("Elevação exige uma solicitação manual confirmada; execução normal ou automática recusada.");
            if (action is ScriptAction.Stop or ScriptAction.Restart or ScriptAction.Recycle && !confirmed) return Result.Fail("Cancelado. Nenhuma alteração feita.");
            // Stored paths need only lexical validity; effects require a fresh canonical local identity.
            entry = entry.Copy(); entry.Path = Paths.Normalize(entry.Path);
            var snapshot = await processes.Snapshot(token);
            var allowed = Policy.Check(entry, snapshot, action, File.Exists(entry.Path)); if (!allowed.Success) return allowed;
            if (action is ScriptAction.Start or ScriptAction.Restart)
            {
                var ready = processes.Preflight(entry); if (!ready.Success) return ready;
            }
            token.ThrowIfCancellationRequested();
            var currentAuthority = effectAuthority?.Invoke(entry, action); if (currentAuthority is { Success: false }) return currentAuthority;
            if (action == ScriptAction.Recycle) return recycler.Recycle(entry.Path);
            if (action is ScriptAction.Stop or ScriptAction.Restart)
            {
                var stopped = await processes.Stop(snapshot.For(entry.Path)[0], token); if (!stopped.Success || action == ScriptAction.Stop) return stopped;
                // Once stopped, do not hide a partial restart failure behind a cancellation exception.
                if (token.IsCancellationRequested) return Result.Fail("Processo encerrado. Reinício cancelado antes de iniciar.");
                snapshot = await processes.Snapshot(token);
                allowed = Policy.Check(entry, snapshot, ScriptAction.Start, File.Exists(entry.Path));
                if (!allowed.Success) return Result.Fail("Processo encerrado. Novo início recusado: " + allowed.Message);
                var ready = processes.Preflight(entry); if (!ready.Success) return Result.Fail("Processo encerrado. Novo início recusado: " + ready.Message);
            }
            var started = await processes.Start(entry, token);
            return action == ScriptAction.Restart && !started.Success ? Result.Fail("Processo encerrado. Falha ao reiniciar: " + started.Message) : started;
        }
        catch (OperationCanceledException) { return Result.Fail("Cancelado; atualize para conferir o estado atual."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { return Result.Fail("Operação recusada pelo Windows. Atualize ou confira as permissões."); }
        finally { gate.Release(); }
    }
}

public sealed class WindowsProcesses : IProcessAccess
{
    readonly Func<int, IStopHandle> openStop;
    readonly Func<CancellationToken, Task<Snapshot>>? fixtureSnapshot;
    public WindowsProcesses() : this(pid => new Native.StopHandle(Native.OpenProcess(Native.QueryAccess | 0x0001, false, pid))) { }
    internal WindowsProcesses(Func<int, IStopHandle> openStop, Func<CancellationToken, Task<Snapshot>>? fixtureSnapshot = null) { this.openStop = openStop; this.fixtureSnapshot = fixtureSnapshot; }
    internal Func<Result>? StartAuthority { get; set; }
    public Result Preflight(ScriptEntry entry) => Discovery.Compatible(entry);
    public Task<Snapshot> Snapshot(CancellationToken token)
    {
        if (fixtureSnapshot is not null) return fixtureSnapshot(token);
        List<ProcessIdentity> found = []; bool complete = true;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                token.ThrowIfCancellationRequested();
                int pid = 0;
                try
                {
                    if (!process.ProcessName.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase)) continue;
                    pid = process.Id;
                    using var handle = Native.OpenProcess(Native.QueryAccess, false, pid);
                    if (handle.IsInvalid || !Native.Observe(handle, pid, out var observed)) { found.Add(new(pid, 0, "", null)); complete = false; continue; }
                    found.Add(observed!);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { if (pid > 0) found.Add(new(pid, 0, "", null)); complete = false; }
            }
        }
        return Task.FromResult(new Snapshot(found, complete, complete ? "Consulta nativa atual completa para processos nomeados AutoHotkey." : "Visibilidade parcial: há AHK elevado/inacessível, estrangeiro ou consulta indisponível.", System.IO.Path.Combine(Discovery.DefaultRoot, "startup.ahk")));
    }
    public async Task<Result> Start(ScriptEntry entry, CancellationToken token)
    {
        if (entry.RunAsAdministrator) return Result.Fail("Preferência de administrador exige início manual com UAC; sem fallback normal.");
        var gate = StartAuthority?.Invoke() ?? Result.Fail("Autoridade de início nativo não fornecida."); if (!gate.Success) return gate;
        var compatible = Discovery.Compatible(entry); if (!compatible.Success) return compatible;
        var recent = await Snapshot(token); var allowed = Policy.Check(entry, recent, ScriptAction.Start, File.Exists(entry.Path)); if (!allowed.Success) return allowed;
        token.ThrowIfCancellationRequested();
        try
        {
            gate = StartAuthority?.Invoke() ?? Result.Fail("Autoridade de início perdida."); if (!gate.Success) return gate;
            using var process = Process.Start(Commands.Launch(entry.Interpreter, entry.Path));
            return process is null ? Result.Fail("O Windows não iniciou o interpretador.") : Result.Ok("Início solicitado. Atualize para confirmar a execução.");
        }
        catch (Win32Exception ex) { return Result.Fail(ex.NativeErrorCode == 740 ? "Requer elevação. Use a preferência Executar como administrador e Iniciar manualmente. O gerenciador permanece normal." : "O Windows recusou o início do interpretador."); }
    }
    public Task<Result> Prove(ProcessIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = Native.OpenProcess(Native.QueryAccess, false, identity.Pid);
        bool proven = !handle.IsInvalid && Native.Observe(handle, identity.Pid, out var observed) && Native.Matches(identity, observed);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(proven ? Result.Ok("Identidade positiva do processo revalidada no mesmo handle de consulta.") : Result.Fail("Script, interpretador ou objeto atual não puderam ser correlacionados."));
    }
    public async Task<Result> Stop(ProcessIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity.Pid <= 0 || identity.Created <= 0 || identity.Script is null) return Result.Fail("Identidade de processo insuficiente.");
        using var handle = openStop(identity.Pid);
        if (handle.Invalid) return Result.Fail("Processo elevado, inacessível ou encerrado.");
        token.ThrowIfCancellationRequested();
        // The command and exact identity are read on the terminate handle immediately before the effect.
        if (!Native.Observe(handle, identity.Pid, out var observed) || !Native.Matches(identity, observed)) return Result.Fail("A identidade mudou ou não pôde ser revalidada. Nenhum processo encerrado.");
        token.ThrowIfCancellationRequested();
        if (!handle.Terminate()) return Result.Fail("O Windows recusou encerrar o processo verificado.");
        var wait = await Task.Run(() => handle.Wait(5000));
        return wait == 0 ? Result.Ok("Instância verificada encerrada. Fontes externas de inicialização continuam configuradas.") : Result.Fail("Encerramento solicitado, mas saída não confirmada. Não inicie uma duplicata.");
    }
}

public static class Native
{
    internal const uint QueryAccess = 0x1000 | 0x00100000;
    internal const int CommandCapacity = 65536;
    public static bool CimCreationMatches(long native, long cim) => native > 0 && cim > 0 && native - native % 10 == cim;
    [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeProcessHandle handle, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(SafeProcessHandle handle, uint exit);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern uint GetProcessId(SafeProcessHandle handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out int session, int length, out int returned);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(SafeProcessHandle handle, int information, IntPtr buffer, int length, out int returned);
    public static bool Identity(SafeProcessHandle handle, out long created, out string executable)
    {
        executable = ""; if (!GetProcessTimes(handle, out created, out _, out _, out _)) return false;
        StringBuilder buffer = new(32768); int length = buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) return false;
        executable = buffer.ToString(); return true;
    }
    internal static bool Matches(ProcessIdentity requested, ProcessIdentity? observed) => requested.Pid > 0 && requested.Created > 0 && requested.Script is not null && observed is not null && requested.Pid == observed.Pid && requested.Created == observed.Created && Paths.Equal(requested.Executable, observed.Executable) && Paths.Equal(requested.Script, observed.Script);
    internal static bool Observe(SafeProcessHandle handle, int pid, out ProcessIdentity? observed)
        => Observe(new HeldObservation(handle), pid, out observed);
    internal static bool Observe(IHeldObservation handle, int pid, out ProcessIdentity? observed)
    {
        observed = null;
        try
        {
            using var requester = WindowsIdentity.GetCurrent();
            using var self = Process.GetCurrentProcess();
            return Observe(handle, pid, requester.User?.Value ?? "", self.SessionId, out observed);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
    internal static bool Observe(IHeldObservation handle, int pid, string sid, int session, out ProcessIdentity? observed)
    {
        observed = null;
        if (pid <= 0 || sid.Length == 0 || !handle.Live || !handle.Read(out var before) || before.Pid != pid || before.Created <= 0 || before.Sid != sid || before.Session != session) return false;
        string? command = handle.Command();
        // Do not turn a readable command attached to a stale or foreign object into identity.
        if (command is null || !handle.Read(out var after) || !SameStamp(before, after) || !handle.Live) return false;
        string? script = Script(command, before.Executable);
        if (script is null) return false;
        // Parsing canonical paths can perform file IO. Recheck liveness and identity after it too.
        if (!handle.Read(out var final) || !SameStamp(final, before) || !handle.Live) return false;
        observed = new(pid, before.Created, before.Executable, script, StableElevation(before.Elevated, after.Elevated, final.Elevated));
        return true;
    }
    // Runtime privilege is display metadata; it cannot authorize or invalidate core identity.
    internal static bool SameStamp(NativeStamp first, NativeStamp second) => first.Pid == second.Pid && first.Created == second.Created && first.Executable == second.Executable && first.Sid == second.Sid && first.Session == second.Session;
    internal static bool? StableElevation(bool? before, bool? after, bool? final) => before.HasValue && before == after && before == final ? before : null;
    internal static bool? DecodeElevation(bool success, int returned, int value) => success && returned == sizeof(int) ? value != 0 : null;
    static bool? Elevation(SafeAccessTokenHandle token)
    {
        try { return DecodeElevation(GetTokenInformation(token, 20, out var value, sizeof(int), out var returned), returned, value); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
    internal static string? Script(string command, string executable)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768 || command.Contains('\0') || char.IsWhiteSpace(command[0])) return null;
        bool quoted = false; int slashes = 0;
        foreach (char c in command)
        {
            if (c == '"' && slashes % 2 == 0) quoted = !quoted;
            slashes = c == '\\' ? slashes + 1 : 0;
        }
        if (quoted) return null;
        var args = Commands.Split(command);
        if (args.Length == 0 || !Commands.IsInterpreter(executable) || !Paths.Equal(args[0], executable)) return null;
        return Commands.Script(command);
    }
    internal static bool CommandRange(int length, int maximum, long offset, int returned, int capacity, int pointerSize)
    {
        int header = pointerSize == 8 ? 16 : pointerSize == 4 ? 8 : 0;
        return header != 0 && capacity > 0 && capacity <= CommandCapacity && returned >= header && returned <= capacity && length > 0 && (length & 1) == 0 && (maximum & 1) == 0 && maximum >= length && offset >= header && (offset & 1) == 0 && offset <= returned && maximum <= returned - offset;
    }
    internal static string? DecodeCommand(IntPtr buffer, int returned, int capacity)
    {
        int header = IntPtr.Size == 8 ? 16 : 8;
        if (buffer == IntPtr.Zero || returned < header || returned > capacity || capacity > CommandCapacity) return null;
        int length = (ushort)Marshal.ReadInt16(buffer);
        int maximum = (ushort)Marshal.ReadInt16(buffer, 2);
        IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
        long offset;
        try { offset = checked(text.ToInt64() - buffer.ToInt64()); }
        catch (OverflowException) { return null; }
        if (!CommandRange(length, maximum, offset, returned, capacity, IntPtr.Size)) return null;
        string? command = Marshal.PtrToStringUni(text, length / 2);
        return command is not null && !command.Contains('\0') ? command : null;
    }
    internal static string? Command(SafeProcessHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(CommandCapacity);
        try
        {
            int status = NtQueryInformationProcess(handle, 60, buffer, CommandCapacity, out int returned);
            // Only a complete success is accepted, including on Windows versions without class 60.
            return status == 0 ? DecodeCommand(buffer, returned, CommandCapacity) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal sealed class StopHandle(SafeProcessHandle handle) : IStopHandle
    {
        readonly HeldObservation observation = new(handle);
        public bool Invalid => handle.IsInvalid;
        public bool Live => observation.Live;
        public bool Read(out NativeStamp stamp) => observation.Read(out stamp);
        public string? Command() => observation.Command();
        public bool Terminate() => TerminateProcess(handle, 0);
        public uint Wait(uint milliseconds) => WaitForSingleObject(handle, milliseconds);
        public void Dispose() => handle.Dispose();
    }
    sealed class HeldObservation(SafeProcessHandle handle) : IHeldObservation
    {
        public bool Live => WaitForSingleObject(handle, 0) == 258;
        public string? Command() => Native.Command(handle);
        public bool Read(out NativeStamp stamp)
        {
            stamp = default;
            if (!Identity(handle, out var created, out var executable) || !OpenProcessToken(handle, 8, out var token)) return false;
            using (token)
            {
                using var owner = new WindowsIdentity(token.DangerousGetHandle());
                if (!GetTokenInformation(token, 12, out var session, sizeof(int), out var returned) || returned != sizeof(int)) return false;
                // TOKEN_ELEVATION contains one DWORD on both x86 and x64. Query only the held target token.
                stamp = new((int)GetProcessId(handle), created, executable, owner.User?.Value ?? "", session, Elevation(token));
                return true;
            }
        }
    }
}

internal readonly record struct NativeStamp(int Pid, long Created, string Executable, string Sid, int Session, bool? Elevated = null);
internal interface IHeldObservation
{
    bool Live { get; }
    bool Read(out NativeStamp stamp);
    string? Command();
}
internal interface IStopHandle : IHeldObservation, IDisposable
{
    bool Invalid { get; }
    bool Terminate();
    uint Wait(uint milliseconds);
}

public sealed class WindowsRecycler : IRecycler
{
    public Result Recycle(string path)
    {
        IFileOperation? operation = null; IntPtr item = IntPtr.Zero;
        try
        {
            // Reject network/removable paths and links, and never offer a permanent-delete fallback.
            if (new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType != DriveType.Fixed || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return Result.Fail("Lixeira recusada para este disco ou link.");
            var clsid = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"); var iid = new Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out operation));
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x0400); // RECYCLEONDELETE, ADDUNDORECORD, EARLYFAILURE, NOERRORUI.
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            var sink = new RecycleSink(); operation.DeleteItem(item, sink); operation.PerformOperations(); operation.GetAnyOperationsAborted(out var aborted);
            return !aborted && sink.Recycled ? Result.Ok("Arquivo enviado à lixeira. Registro preservado; inicialização externa continua configurada.") : Result.Fail("Lixeira cancelada ou recusada. Registro preservado.");
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return Result.Fail("Lixeira indisponível ou recusada. Não há exclusão permanente alternativa."); }
        finally { if (item != IntPtr.Zero) Marshal.Release(item); if (operation is not null) Marshal.FinalReleaseComObject(operation); }
    }
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IFileOperation operation);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);
}

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFileOperation
{
    void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr window);
    void ApplyPropertiesToItem(IntPtr item); void ApplyPropertiesToItems(IntPtr items);
    void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void MoveItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void MoveItems(IntPtr items, IntPtr folder);
    void CopyItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void CopyItems(IntPtr items, IntPtr folder);
    void DeleteItem(IntPtr item, IFileOperationProgressSink sink); void DeleteItems(IntPtr items);
    void NewItem(IntPtr folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
    void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations(); [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item); [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
    [PreserveSig] int PreNewItem(uint flags, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostNewItem(uint flags, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr created);
    [PreserveSig] int UpdateProgress(uint total, uint completed); [PreserveSig] int ResetTimer(); [PreserveSig] int PauseTimer(); [PreserveSig] int ResumeTimer();
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
sealed class RecycleSink : IFileOperationProgressSink
{
    public bool Recycled { get; private set; }
    public int PreDeleteItem(uint flags, IntPtr item) => (flags & 0x80) != 0 ? 0 : unchecked((int)0x80004004); // TSF_DELETE_RECYCLE_IF_POSSIBLE; refuse a permanent operation.
    public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created) { Recycled = result >= 0 && created != IntPtr.Zero && (flags & 0x80) != 0; return 0; }
    public int StartOperations() => 0; public int FinishOperations(int result) => 0;
    public int PreRenameItem(uint flags, IntPtr item, string name) => unchecked((int)0x80004004); public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr created) => 0;
    public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, string name) => unchecked((int)0x80004004); public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, string name, int result, IntPtr created) => 0;
    public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, string name) => unchecked((int)0x80004004); public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, string name, int result, IntPtr created) => 0;
    public int PreNewItem(uint flags, IntPtr folder, string name) => unchecked((int)0x80004004); public int PostNewItem(uint flags, IntPtr folder, string name, string template, uint attributes, int result, IntPtr created) => 0;
    public int UpdateProgress(uint total, uint completed) => 0; public int ResetTimer() => 0; public int PauseTimer() => 0; public int ResumeTimer() => 0;
}
