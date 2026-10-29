using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class LaunchProofChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var proofs = new Dictionary<int, HeldProcess>();
        var handles = new List<NativeProcessIdentity>();
        HeldProcess Receipt(int key, int nativePid)
        {
            var handle = new NativeProcessIdentity(nativePid);
            handles.Add(handle);
            return new(
                handle,
                new(
                    new(
                        key,
                        key,
                        @"C:\fixture\AutoHotkey64.exe",
                        @"C:\fixture\inert.ahk",
                        "S-1-fixture",
                        1
                    ),
                    ExecutionOrigin.Manager,
                    "2.0",
                    DateTimeOffset.UnixEpoch
                )
            );
        }
        static bool Disposed(NativeProcessIdentity handle)
        {
            try
            {
                _ = handle.Live;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
        try
        {
            var live = Receipt(1, Environment.ProcessId);
            var uncertain = Receipt(2, int.MaxValue);
            proofs.Add(1, live);
            proofs.Add(2, uncertain);
            check(
                !uncertain.Handle.TryRead(out _) && !uncertain.Handle.Exited,
                "receipt unreadability is not confirmed exit"
            );
            ProcessManager.PruneExitedLaunchProofs(proofs);
            check(
                proofs.Count == 2 && !Disposed(live.Handle) && !Disposed(uncertain.Handle),
                "live and uncertain launch receipts retain their handles"
            );
            int maximumRetained = proofs.Count;
            for (int cycle = 0; cycle < 128; cycle++)
            {
                int key = cycle + 3;
                var ended = Receipt(key, Environment.ProcessId);
                proofs.Add(key, ended);
                // Inject an observed exit for this receipt only; no process is started or stopped.
                ProcessManager.PruneExitedLaunchProofs(
                    proofs,
                    candidate => ReferenceEquals(candidate, ended.Handle)
                );
                maximumRetained = Math.Max(maximumRetained, proofs.Count);
            }
            int closed = handles.Skip(2).Count(Disposed);
            Console.WriteLine(
                $"RECEIPTS cycles=128;retained={proofs.Count};maximumRetained={maximumRetained};closed={closed}"
            );
            check(
                proofs.Count == 2 && maximumRetained == 2 && closed == 128,
                "repeated confirmed exits keep receipt count bounded and close every ended handle"
            );
            check(
                proofs.ContainsKey(1)
                    && proofs.ContainsKey(2)
                    && !Disposed(live.Handle)
                    && !Disposed(uncertain.Handle),
                "pruning exited receipts preserves independent live and uncertain provenance"
            );
            ProcessManager.PruneExitedLaunchProofs(proofs, _ => false);
            check(
                proofs.Count == 2,
                "repeated observation cannot reinterpret unreadable receipts as exited"
            );
        }
        finally
        {
            foreach (var handle in handles)
                handle.Dispose();
        }
    }
}
