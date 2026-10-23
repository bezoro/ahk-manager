using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

public sealed record ObservedProcesses(ImmutableArray<ProcessSnapshot> Processes, bool Complete, ImmutableArray<Issue> Issues);

public sealed record ProcessCommandRow(int Pid, string Name, string? ExecutablePath, string? CommandLine,
    long? CreationFileTime, int? SessionId);
public sealed record ObservedProcessCommands(ImmutableArray<ProcessCommandRow> Rows, bool Complete, ImmutableArray<Issue> Issues);
public interface IProcessObservationSource
{
    // Synchronous Windows boundary; ProcessManager always invokes it off the UI thread.
    ObservedProcessCommands Read(CancellationToken ct);
}
public sealed class WmiProcessObservationSource : IProcessObservationSource
{
    public ObservedProcessCommands Read(CancellationToken ct)
    {
        var rows = ImmutableArray.CreateBuilder<ProcessCommandRow>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT ProcessId,CreationDate,ExecutablePath,CommandLine,Name,SessionId FROM Win32_Process");
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = search.Get();
            foreach (ManagementObject row in results)
            using (row)
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= 8192) return new(rows.ToImmutable(), false, [new(ReasonCode.ObservationIncomplete)]);
                long? created = null;
                if (row["CreationDate"] is string date)
                    try { created = ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime().ToFileTimeUtc(); }
                    catch (ArgumentException) { }
                rows.Add(new(checked((int)(uint)row["ProcessId"]), row["Name"] as string ?? "",
                    row["ExecutablePath"] as string, row["CommandLine"] as string, created,
                    row["SessionId"] is uint session ? checked((int)session) : null));
            }
            return new(rows.ToImmutable(), true, []);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        { return new(rows.ToImmutable(), false, [new(ReasonCode.ObservationIncomplete, ex.Message)]); }
    }
}

internal sealed record HeldProcess(NativeProcessIdentity Handle, ProcessSnapshot Snapshot);

public sealed class ProcessObservation
{
    internal static List<HeldProcess> Read(IReadOnlyCollection<string> knownInterpreters, out bool complete, out ImmutableArray<Issue> issues, CancellationToken ct, IProcessObservationSource? source = null)
    {
        complete = true; var found = new List<HeldProcess>(); var problems = ImmutableArray.CreateBuilder<Issue>();
        using var self = Process.GetCurrentProcess();
        using var current = WindowsIdentity.GetCurrent();
        string sid = current.User?.Value ?? "";
        Dictionary<string, InterpreterInfo?> versions = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var input = (source ?? new WmiProcessObservationSource()).Read(ct);
            complete = input.Complete; problems.AddRange(input.Issues);
            foreach (var row in input.Rows)
            {
                ct.ThrowIfCancellationRequested();
                string name = row.Name;
                string? executable = row.ExecutablePath;
                bool candidate = name.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase) || knownInterpreters.Any(p => ScriptPaths.Equal(p, executable));
                InterpreterInfo? interpreter = null;
                if (executable is not null)
                {
                    if (!versions.TryGetValue(executable, out interpreter))
                    {
                        try
                        {
                            // Product metadata also identifies renamed interpreters; basename alone is not proof.
                            var version = FileVersionInfo.GetVersionInfo(executable);
                            if (version.ProductName?.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase) == true) { candidate = true; interpreter = AutoHotkeyDiscovery.Validate(executable); }
                        }
                        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { }
                        versions[executable] = interpreter;
                    }
                    if (interpreter is not null) candidate = true;
                }
                if (!candidate) continue;
                if (row.SessionId is int session && session != self.SessionId) continue;
                if (interpreter is null || row.CommandLine is not string command || row.CreationFileTime is not long cimCreated) { complete = false; continue; }
                int pid = row.Pid;
                var held = new NativeProcessIdentity(pid); bool adopted = false;
                try
                {
                    if (!held.TryRead(out var before) || before.Sid != sid || before.Session != self.SessionId || !ScriptPaths.Equal(before.Executable, interpreter.Path)) { complete = false; continue; }
                    if (before.Created - before.Created % 10 != cimCreated) { complete = false; continue; }
                    string? script = ScriptPaths.ScriptFromCommand(command, before.Executable);
                    if (script is null || !held.TryRead(out var after) || before != after || !held.Live) { complete = false; continue; }
                    string scriptHash = ScriptPaths.Fingerprint(script);
                    var identity = new ProcessIdentity(pid, before.Created, before.Executable, script, sid, self.SessionId, scriptHash, interpreter.Hash);
                    nint window = AhkWindowControl.FindWindow(identity);
                    if (window == 0 || !held.Matches(identity)) { complete = false; continue; }
                    found.Add(new(held, new(identity, ExecutionOrigin.External, interpreter.Version, DateTimeOffset.FromFileTime(before.Created), WindowVerified: true)));
                    adopted = true;
                }
                catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception or ManagementException or InvalidOperationException) { complete = false; }
                finally { if (!adopted) held.Dispose(); }
            }
        }
        catch (OperationCanceledException)
        { foreach (var item in found) item.Handle.Dispose(); throw; }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        { complete = false; problems.Add(new(ReasonCode.ObservationIncomplete, ex.Message)); }
        catch { foreach (var item in found) item.Handle.Dispose(); throw; }
        if (!complete && !problems.Any()) problems.Add(new(ReasonCode.ObservationIncomplete));
        issues = problems.ToImmutable();
        return found;
    }
}
