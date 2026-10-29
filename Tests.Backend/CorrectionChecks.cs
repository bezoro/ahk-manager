using System.Diagnostics;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Tests.Backend;

internal static class CorrectionChecks
{
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateHardLinkW"
    )]
    static extern bool CreateHardLink(string link, string existing, nint reserved);

    static ParentProof Parent =>
        new(100, 1, @"C:\fixture\Manager.exe", 200, 2, "S-1-fixture", 1, AuthorityMode.Normal);

    internal static async Task RunAsync(string root)
    {
        int failures = 0,
            checks = 0;
        void Check(bool pass, string label)
        {
            checks++;
            Console.WriteLine((pass ? "PASS " : "FAIL ") + label);
            if (!pass)
                failures++;
        }
        ShellPaths(root, Check);
        LaunchProofChecks.Run(Check);
        string isolatedOwner = "S-1-correction-" + Guid.NewGuid().ToString("N");
        string operational = Path.Combine(root, "operational"),
            chromium = Path.Combine(root, "roaming", "Chromium");
        Directory.CreateDirectory(operational);
        Directory.CreateDirectory(Path.Combine(chromium, "Default"));
        string preferences = Path.Combine(chromium, "Default", "Preferences");
        File.WriteAllText(preferences, "{}");
        using (
            var lease = new FileStream(
                preferences,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            )
        )
            Check(
                !ManagerElevation.ProfileLocksAvailable(operational, isolatedOwner, chromium),
                "F1 actual Chromium held Preferences blocks activation"
            );
        Check(
            !ManagerElevation.ProfileLocksAvailable(
                operational,
                isolatedOwner,
                Path.Combine(root, "missing-Chromium")
            ),
            "F1 missing Chromium path cannot prove released locks"
        );
        Check(
            ManagerElevation.ProfileLocksAvailable(operational, isolatedOwner, chromium),
            "F1 released actual Chromium locks permit next gate"
        );
        Check(
            ManagerElevation.ChromiumRoot
                == Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Bezoro",
                    "AhkManagerElectron",
                    "Chromium"
                )
                && ManagerElevation.ProfileRootsMatch(
                    ManagerElevation.ProfileRoot,
                    ManagerElevation.ChromiumRoot
                ),
            "F1 actual fixed Roaming Chromium path is bound separately from operational root"
        );
        Check(
            !ManagerElevation.ProfileRootsMatch(ManagerElevation.ProfileRoot, operational),
            "F1 wrong existing Chromium directory cannot authenticate receipt"
        );

        using var self = Process.GetCurrentProcess();
        int reads = 0;
        bool originalHeld = true;
        long? Clock(SafeProcessHandle _)
        {
            reads++;
            return originalHeld ? 7 : 8;
        }
        using var retained = UnelevatedProcessLauncher.RetainCreationReceipt(
            self.SafeHandle,
            self.Id,
            Process.GetProcessById,
            Clock
        );
        Check(
            retained is not null && reads >= 2,
            "F2 returned Process handle is acquired and verified while creation handle remains held"
        );
        originalHeld = false;
        reads = 0;
        long? Reused(SafeProcessHandle _) => ++reads == 1 ? 7 : 8;
        using var reused = UnelevatedProcessLauncher.RetainCreationReceipt(
            self.SafeHandle,
            self.Id,
            Process.GetProcessById,
            Reused
        );
        Check(reused is null, "F2 reused creation stamp cannot return another Process");
        using var exited = UnelevatedProcessLauncher.RetainCreationReceipt(
            self.SafeHandle,
            self.Id,
            Process.GetProcessById,
            _ => null
        );
        Check(exited is null, "F2 exited or unreadable creation receipt fails closed");
        using var nativeRetained = UnelevatedProcessLauncher.RetainCreationReceipt(
            self.SafeHandle,
            self.Id
        );
        Check(
            nativeRetained is not null && !nativeRetained.SafeHandle.IsInvalid,
            "F2 actual matching native creation receipt returns already-open handle"
        );

        int dispatches = 0;
        using var denied = new ManagerElevation(
            Parent,
            () => true,
            owner =>
            {
                dispatches++;
                owner.ObserveDenied();
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask
        );
        Guid first = Guid.NewGuid(),
            second = Guid.NewGuid();
        denied.Begin(first);
        await denied.Completion;
        bool retry = true;
        try
        {
            denied.Begin(second);
            await denied.Completion;
        }
        catch (ProtocolException)
        {
            retry = false;
        }
        Check(
            retry && dispatches == 2,
            "F3 fresh explicit ID after terminal denial may dispatch once"
        );
        denied.Begin(first);
        Check(dispatches == (retry ? 2 : 1), "F3 historical operation ID cannot redispatch");
        var unwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unwinding = new ManagerElevation(
            Parent,
            () => true,
            async owner =>
            {
                owner.ObserveDenied();
                await unwind.Task;
            },
            (_, _) => Task.CompletedTask
        );
        Guid old = Guid.NewGuid(),
            busyId = Guid.NewGuid();
        var pending = unwinding.Begin(old);
        var terminal = await unwinding.Status(old, pending.Revision, CancellationToken.None);
        var busy = unwinding.Begin(busyId);
        Check(
            terminal.CanResumeNormal
                && busy.OperationId == busyId
                && busy.State == ElevationState.Failed
                && busy.CanResumeNormal,
            "F3 safe terminal worker unwind returns explicit effect-free retry refusal"
        );
        unwind.SetResult();
        await unwinding.Completion;
        Check(
            unwinding.Begin(busyId) == busy
                && (await unwinding.Status(old, -0L, CancellationToken.None)).State
                    == ElevationState.Denied,
            "F3 old IDs retain readonly terminal history without replay"
        );

        using var prep = new ManagerElevation(
            Parent,
            () => true,
            () => throw new IOException("injected pipe preparation failure"),
            () =>
            {
                dispatches++;
                return null;
            }
        );
        int before = dispatches;
        prep.Begin(Guid.NewGuid());
        await prep.Completion;
        Check(
            dispatches == before
                && prep.Snapshot.CanResumeNormal
                && prep.Snapshot.State == ElevationState.Failed,
            "F4 pre-dispatch preparation failure safely resumes normal"
        );
        ManagerElevation? cancelled = null;
        Guid cancelId = Guid.NewGuid();
        int late = 0;
        using (
            cancelled = new ManagerElevation(
                Parent,
                () => true,
                () => cancelled!.Cancel(cancelId),
                () =>
                {
                    late++;
                    return null;
                }
            )
        )
        {
            cancelled.Begin(cancelId);
            await cancelled.Completion;
            Check(
                late == 0
                    && cancelled.Snapshot.CanResumeNormal
                    && cancelled.Snapshot.State == ElevationState.Cancelled,
                "F4 cancel before dispatch cannot open late UAC"
            );
        }
        using var unknown = new ManagerElevation(
            Parent,
            () => true,
            () => { },
            () => throw new IOException("dispatch result unknown")
        );
        unknown.Begin(Guid.NewGuid());
        await unknown.Completion;
        bool blocked = false;
        try
        {
            unknown.Begin(Guid.NewGuid());
        }
        catch (ProtocolException)
        {
            blocked = true;
        }
        Check(
            unknown.Snapshot.State == ElevationState.Unknown
                && !unknown.Snapshot.CanResumeNormal
                && blocked,
            "F4 post-dispatch uncertainty remains frozen and forbids a fresh launch"
        );
        await Tests.Backend.Core.CoreSuite.RunAdministratorRestartChecksAsync(root, Check);
        Console.WriteLine($"CORRECTIONS checks={checks};failures={failures}");
        if (failures != 0)
            throw new InvalidOperationException($"Correction regressions failed: {failures}");
    }

    static void ShellPaths(string root, Action<bool, string> check)
    {
        string expected = Path.GetFullPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe"
            )
        );
        bool accepted = false;
        try
        {
            accepted = ScriptPaths.Equal(
                UnelevatedProcessLauncher.ExpectedExplorerPath(),
                expected
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                "S1 baseline path rejection: " + ex.GetType().Name + "/" + ex.Message
            );
        }
        check(
            accepted,
            "S1 legitimate system Explorer path permits its existing multiple hard links"
        );
        if (!accepted)
            return;
        static bool Rejected(Func<string> path)
        {
            try
            {
                _ = path();
                return false;
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return true;
            }
        }
        string fixtures = Path.Combine(root, "shell-paths"),
            windows = Path.Combine(fixtures, "Windows");
        Directory.CreateDirectory(windows);
        string source = Path.Combine(fixtures, "inert-source.bin"),
            shell = Path.Combine(windows, "explorer.exe");
        File.WriteAllText(source, "Owned inert file; metadata fixtures only.");
        if (!CreateHardLink(shell, source, 0))
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error()
            );
        check(
            ScriptPaths.Equal(UnelevatedProcessLauncher.ExpectedExplorerPath(windows), shell),
            "S1 owned regular multi-link expected shell file is accepted"
        );
        string strictScript = Path.Combine(fixtures, "strict-script.ahk"),
            strictInterpreter = Path.Combine(fixtures, "strict-interpreter.exe");
        if (
            !CreateHardLink(strictScript, source, 0)
            || !CreateHardLink(strictInterpreter, source, 0)
        )
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error()
            );
        check(
            Rejected(() => ScriptPaths.Canonical(strictScript)),
            "S1 shared strict script normalization still rejects owned hard links"
        );
        check(
            Rejected(() => ScriptPaths.Canonical(strictInterpreter, false)),
            "S1 shared strict interpreter normalization still rejects owned hard links"
        );
        check(
            Rejected(() =>
                UnelevatedProcessLauncher.ExpectedExplorerPath(Path.Combine(fixtures, "missing"))
            ),
            "S1 missing expected shell file is refused"
        );
        string wrong = Path.Combine(fixtures, "wrong-name");
        Directory.CreateDirectory(wrong);
        File.WriteAllText(Path.Combine(wrong, "other.exe"), "inert");
        check(
            Rejected(() => UnelevatedProcessLauncher.ExpectedExplorerPath(wrong)),
            "S1 another filename cannot replace expected explorer.exe"
        );
        string directory = Path.Combine(fixtures, "directory-leaf");
        Directory.CreateDirectory(Path.Combine(directory, "explorer.exe"));
        check(
            Rejected(() => UnelevatedProcessLauncher.ExpectedExplorerPath(directory)),
            "S1 expected shell path must be a regular file"
        );
        check(
            Rejected(() => UnelevatedProcessLauncher.ExpectedExplorerPath("relative-windows")),
            "S1 relative shell directory is refused before file access"
        );
        check(
            Rejected(() => UnelevatedProcessLauncher.ExpectedExplorerPath(@"\\server\Windows")),
            "S1 remote shell directory is refused before network file access"
        );
        string leaf = Path.Combine(fixtures, "file-reparse");
        Directory.CreateDirectory(leaf);
        File.CreateSymbolicLink(Path.Combine(leaf, "explorer.exe"), source);
        check(
            Rejected(() => UnelevatedProcessLauncher.ExpectedExplorerPath(leaf)),
            "S1 owned reparse file cannot become expected shell image"
        );
        string ancestor = Path.Combine(fixtures, "ancestor-reparse");
        Directory.CreateSymbolicLink(ancestor, windows);
        check(
            Rejected(() => UnelevatedProcessLauncher.ExpectedExplorerPath(ancestor)),
            "S1 owned reparse ancestor cannot redirect expected shell image"
        );
    }
}
