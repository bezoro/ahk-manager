using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Core.Infrastructure;

public sealed class ProcessObservation
{
    internal static List<HeldProcess> Read(
        IReadOnlyCollection<string> knownInterpreters,
        out bool complete,
        out ImmutableArray<Issue> issues,
        CancellationToken ct,
        IProcessObservationSource? source = null,
        IReadOnlyCollection<HeldProcess>? launchProofs = null,
        Func<NativeProcessIdentity, string?>? readImage = null
    )
    {
        complete = true;
        var found = new List<HeldProcess>();
        var problems = ImmutableArray.CreateBuilder<Issue>();
        using var self = Process.GetCurrentProcess();
        using var current = WindowsIdentity.GetCurrent();
        string sid = current.User?.Value ?? "";
        Dictionary<string, InterpreterInfo> versions = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var input = (source ?? new WmiProcessObservationSource()).Read(ct);
            complete = input.Complete;
            problems.AddRange(input.Issues);
            foreach (var row in input.Rows)
            {
                ct.ThrowIfCancellationRequested();
                // Session data is independent of the mutable AHK caption. Native identity is
                // acquired before image classification, including renamed/opaque interpreters.
                if (row.SessionId is int session && session != self.SessionId)
                    continue;
                var held = new NativeProcessIdentity(row.Pid);
                bool adopted = false;
                try
                {
                    if (
                        !ProcessRelevance.ReadCandidate(
                            row,
                            knownInterpreters,
                            held,
                            out string? nativeImage,
                            readImage
                        )
                    )
                        continue;
                    if (!held.TryRead(out var before))
                    {
                        complete = false;
                        continue;
                    }
                    if (before.Sid != sid || before.Session != self.SessionId)
                        continue;
                    string executable = before.Executable;
                    if (nativeImage is not null && !ScriptPaths.Equal(nativeImage, executable))
                    {
                        complete = false;
                        continue;
                    }
                    if (
                        row.ExecutablePath is not null
                        && !ScriptPaths.Equal(row.ExecutablePath, executable)
                    )
                    {
                        complete = false;
                        continue;
                    }
                    if (!versions.TryGetValue(executable, out var interpreter))
                    {
                        interpreter = AutoHotkeyDiscovery.Validate(executable);
                        versions[executable] = interpreter;
                    }
                    string? script = null;
                    if (
                        row.CommandLine is string command
                        && row.CreationFileTime is long cimCreated
                    )
                    {
                        if (before.Created - before.Created % 10 != cimCreated)
                        {
                            complete = false;
                            continue;
                        }
                        script = ScriptPaths.ScriptFromCommand(command, executable);
                    }
                    var launch = launchProofs?.SingleOrDefault(p =>
                        p.Snapshot.Identity.Pid == before.Pid
                        && p.Snapshot.Identity.CreationFileTime == before.Created
                    );
                    if (
                        script is null
                        && launch is not null
                        && launch.Handle.Matches(launch.Snapshot.Identity)
                        && launch.Snapshot.Identity.InterpreterHash == interpreter.Hash
                        && ScriptPaths.Fingerprint(launch.Snapshot.Identity.ScriptPath)
                            == launch.Snapshot.Identity.ScriptHash
                    )
                        script = launch.Snapshot.Identity.ScriptPath;
                    if (
                        script is null
                        || !held.TryRead(out var after)
                        || before != after
                        || !held.Live
                    )
                    {
                        complete = false;
                        continue;
                    }
                    string scriptHash = ScriptPaths.Fingerprint(script);
                    var identity = new ProcessIdentity(
                        row.Pid,
                        before.Created,
                        executable,
                        script,
                        sid,
                        self.SessionId,
                        scriptHash,
                        interpreter.Hash
                    );
                    if (
                        launch is not null
                        && !NativeProcessIdentity.Same(identity, launch.Snapshot.Identity)
                    )
                    {
                        complete = false;
                        continue;
                    }
                    nint window = AhkWindowControl.FindWindow(identity);
                    if (window == 0 || !held.Matches(identity))
                    {
                        complete = false;
                        continue;
                    }
                    found.Add(
                        new(
                            held,
                            new(
                                identity,
                                launch is null ? ExecutionOrigin.External : ExecutionOrigin.Manager,
                                interpreter.Version,
                                DateTimeOffset.FromFileTime(before.Created),
                                WindowVerified: true,
                                ControlUnavailableReason: held.ControlUnavailableReason()
                            )
                        )
                    );
                    adopted = true;
                }
                catch (Exception ex)
                    when (SafeJsonFiles.Known(ex)
                        || ex
                            is System.ComponentModel.Win32Exception
                                or ManagementException
                                or InvalidOperationException
                    )
                {
                    complete = false;
                }
                finally
                {
                    if (!adopted)
                        held.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in found)
                item.Handle.Dispose();
            throw;
        }
        catch (Exception ex)
            when (ex
                    is ManagementException
                        or System.Runtime.InteropServices.COMException
                        or UnauthorizedAccessException
                        or InvalidOperationException
            )
        {
            complete = false;
            problems.Add(new(ReasonCode.ObservationIncomplete, ex.Message));
        }
        catch
        {
            foreach (var item in found)
                item.Handle.Dispose();
            throw;
        }
        if (!complete && !problems.Any())
            problems.Add(new(ReasonCode.ObservationIncomplete));
        issues = problems.ToImmutable();
        return found;
    }
}
