using System.Collections.Immutable;
using System.Diagnostics;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Tests.Backend;

internal static class RuntimeServiceChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "runtime-services"); Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "ação com espaço.ahk");
        await File.WriteAllTextAsync(script, "; inert editor argument fixture only\n");
        string editor = Path.Combine(directory, "Code.exe"); File.Copy(Environment.ProcessPath!, editor);
        string canonicalScript = ScriptPaths.Canonical(script);

        var automatic = ExternalEditorService.CreateEditorStartInfo(script, new(), editor, false);
        check(!automatic.UseShellExecute && automatic.Verb.Length == 0 && ScriptPaths.Equal(automatic.FileName, editor) && automatic.ArgumentList.SequenceEqual([canonicalScript]), "default VS Code receives the entire script path as one argument without shell execution");
        var configured = new ManagerSettings { EditorPath = editor, EditorArguments = ["--reuse-window", "--goto", "{file}:12:4", "--user-data-dir=folder with spaces"] };
        var custom = ExternalEditorService.CreateEditorStartInfo(script, configured, Path.Combine(directory, "ignored.exe"), false);
        check(custom.ArgumentList.SequenceEqual(["--reuse-window", "--goto", canonicalScript + ":12:4", "--user-data-dir=folder with spaces"]), "explicit editor and placeholder arguments take precedence without splitting spaces");
        var appended = ExternalEditorService.CreateEditorStartInfo(script, configured with { EditorArguments = ["--reuse-window"] }, null, false);
        check(appended.ArgumentList.SequenceEqual(["--reuse-window", canonicalScript]), "missing file placeholder appends exactly one script argument");
        var normalFallback = ExternalEditorService.CreateEditorStartInfo(script, new(), null, false);
        check(normalFallback.UseShellExecute && normalFallback.Verb == "edit", "missing VS Code permits only the Windows Edit association rather than opening an AHK file");
        var highFallback = ExternalEditorService.CreateEditorStartInfo(script, new(), null, true);
        check(!highFallback.UseShellExecute && highFallback.FileName.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase) && highFallback.ArgumentList.SequenceEqual([canonicalScript]), "elevated manager selects a concrete text editor for a medium-token launch without a shell verb");

        var visited = new List<string>();
        string? found = ExternalEditorService.DiscoverVisualStudioCode([@"C:\missing\Code.exe", @"C:\missing\CODE.exe", editor], candidate =>
        {
            visited.Add(candidate);
            if (candidate.StartsWith(@"C:\missing\", StringComparison.OrdinalIgnoreCase)) throw new IOException("inert missing candidate");
            return candidate;
        });
        check(found == editor && visited.Count == 2, "VS Code discovery skips invalid candidates and deduplicates Windows paths before continuing");
        check(ExternalEditorService.DiscoverVisualStudioCode([editor], _ => null) is null, "unvalidated editor discovery has no launch candidate");

        string[] arguments = ["--reuse-window", canonicalScript, "quoted \"text\" and trailing slash\\"];
        string command = UnelevatedProcessLauncher.BuildExecutableCommand(editor, arguments);
        check(ScriptPaths.SplitCommand(command).SequenceEqual(arguments.Prepend(editor)), "medium-token editor command roundtrips quotes spaces and trailing backslashes exactly");
        int argumentLengthAtLimit = 1023 - ScriptPaths.QuoteArgument(editor).Length - 3;
        check(argumentLengthAtLimit > 0 && UnelevatedProcessLauncher.BuildExecutableCommand(editor, [new string('x', argumentLengthAtLimit)]).Length == 1023, "medium-token editor accepts exactly 1023 UTF-16 command-line code units");
        bool oneOverLimit = false;
        try { _ = UnelevatedProcessLauncher.BuildExecutableCommand(editor, [new string('x', argumentLengthAtLimit + 1)]); } catch (ArgumentException) { oneOverLimit = true; }
        check(oneOverLimit, "medium-token editor refuses 1024 command-line code units before token creation");
        foreach (var invalid in new[] { new[] { "nul\0argument" }, new[] { new string('x', 32767) } })
        {
            bool refused = false; try { _ = UnelevatedProcessLauncher.BuildExecutableCommand(editor, invalid); } catch (ArgumentException) { refused = true; }
            check(refused, "invalid or oversized editor command is refused before native launch");
        }

        using var self = Process.GetCurrentProcess(); using var native = new NativeProcessIdentity(self.Id);
        if (!native.TryRead(out var stamp)) throw new InvalidOperationException("Own process receipt unavailable for monitor fixture.");
        var identity = new ProcessIdentity(stamp.Pid, stamp.Created, stamp.Executable, script, stamp.Sid, stamp.Session);
        using (var monitor = ProcessManager.TryCreateExitMonitor(identity, (_, _) => { }))
            check(monitor is not null && monitor.Id == self.Id && !monitor.SafeHandle.IsInvalid, "existing external process monitor retains its verified creation handle without launching a process");
        foreach (var changed in new[] { identity with { CreationFileTime = identity.CreationFileTime + 1 }, identity with { UserSid = "different-user" }, identity with { ExecutablePath = editor } })
        {
            using var refused = ProcessManager.TryCreateExitMonitor(changed, (_, _) => { });
            check(refused is null, "stale creation user or executable cannot become an exit monitor");
        }

        var first = new ProcessSnapshot(identity, ExecutionOrigin.External, "2.0", DateTimeOffset.UnixEpoch);
        var second = first with { Identity = identity with { Pid = identity.Pid + 1 } };
        var observation = new ObservedProcesses([first, second], true, []);
        check(ProcessManager.SameObservation(observation, observation with { Processes = [second, first] }), "unchanged inventory order does not invalidate confirmations or reanalyze scripts");
        check(!ProcessManager.SameObservation(observation, observation with { Complete = false }) && !ProcessManager.SameObservation(observation, observation with { Processes = [first with { Suspended = true }, second] }), "inventory certainty and observed script state changes request a new snapshot");
        await PulseLifecycle(check);
    }

    static async Task PulseLifecycle(Action<bool, string> check)
    {
        int available = 0, calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulse = new ProcessObservationPulse(() => Volatile.Read(ref available) != 0, async () =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task;
        }, TimeSpan.FromMilliseconds(20));
        try
        {
            await Task.Delay(70);
            check(Volatile.Read(ref calls) == 0, "busy or unavailable observation skips polling work");
            Volatile.Write(ref available, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(70);
            check(Volatile.Read(ref calls) == 1, "slow inventory polling never overlaps another observation callback");
            var closing = pulse.DisposeAsync().AsTask();
            var closingAgain = pulse.DisposeAsync().AsTask();
            await Task.Delay(30);
            check(!closing.IsCompleted, "observation disposal waits for its in-flight work");
            check(ReferenceEquals(closing, closingAgain) && !closingAgain.IsCompleted, "concurrent observation disposal waits for the same in-flight task");
            release.TrySetResult(); await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(70);
            check(Volatile.Read(ref calls) == 1, "disposed observation timer never schedules another refresh");
        }
        finally { release.TrySetResult(); await pulse.DisposeAsync(); }

        int attempts = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transient = new ProcessObservationPulse(() => true, () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("owned transient read fixture");
            recovered.TrySetResult(); return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(Volatile.Read(ref attempts) >= 2, "known transient observation errors do not fault disposal or stop later polling");
    }
}
