using System.Diagnostics;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.Backend;

internal static class ElevationActivation
{
    internal static void ReportActivationFailure(
        int result,
        bool normalExited,
        bool hostExited,
        bool quietStartup,
        Action report
    )
    {
        if (result != 0 && normalExited && hostExited && !quietStartup)
            report();
    }

    internal static ProcessStartInfo CommittedHostStartInfo(
        string hostPath,
        string? startupAdoption = null,
        bool resumeLogin = false,
        bool backgroundStartup = false
    )
    {
        var start = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
        };
        // This requests only window activation. Authority still comes from native token proof.
        if (resumeLogin && startupAdoption is not null)
            throw new ProtocolException("InvalidRequest");
        if (backgroundStartup && startupAdoption is null)
            throw new ProtocolException("InvalidRequest");
        if (resumeLogin)
        {
            start.ArgumentList.Add("--login");
            start.ArgumentList.Add("--background");
        }
        else if (backgroundStartup)
            start.ArgumentList.Add("--background");
        else
            start.ArgumentList.Add("--foreground");
        if (startupAdoption is not null)
            start.ArgumentList.Add("--startup-adoption=" + startupAdoption);
        return start;
    }

    internal static async Task<int> ActivateCommitted(
        Func<TimeSpan, Task<bool>> waitExit,
        Func<TimeSpan, Task<bool>> waitLocks,
        Func<bool> valid,
        Func<bool> consume,
        Func<bool> launch
    )
    {
        try
        {
            if (
                !valid()
                || !await waitExit(TimeSpan.FromSeconds(60)).ConfigureAwait(false)
                || !valid()
                || !await waitLocks(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                || !valid()
            )
                return 75;
            if (!consume())
                return 75;
            return launch() ? 0 : 75;
        }
        catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex))
        {
            return 75;
        }
    }

    internal static bool ProfileLocksAvailable(string profile, string sid, string? chromium = null)
    {
        try
        {
            chromium ??= ElevationReceiptStore.ChromiumRoot;
            if (!Directory.Exists(profile) || !Directory.Exists(chromium))
                return false;
            using var mutex = new Mutex(false, "Global\\Bezoro.AhkManager.Effects." + sid);
            bool held;
            try
            {
                held = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            if (!held)
                return false;
            try
            {
                foreach (
                    string file in new[]
                    {
                        "SingletonLock",
                        "lockfile",
                        "Local State",
                        "Default/Preferences",
                    }
                )
                {
                    string path = Path.Combine(chromium, file);
                    if (File.Exists(path))
                    {
                        using var lease = new FileStream(
                            ScriptPaths.Canonical(path, false),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.None
                        );
                    }
                }

                return true;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex))
        {
            return false;
        }
    }
}
