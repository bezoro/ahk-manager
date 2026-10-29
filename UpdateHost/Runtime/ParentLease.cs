using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class ParentLease : IDisposable
{
    readonly Process process;

    internal ParentLease(UpdatePlan plan)
    {
        using var self = Process.GetCurrentProcess();
        if (
            Native.NtQueryInformationProcess(
                self.SafeHandle,
                0,
                out var basic,
                Marshal.SizeOf<Native.BasicInformation>(),
                out _
            ) != 0
            || basic.ParentPid.ToInt64() != plan.ParentPid
        )
            throw new InvalidDataException("Update host parent is not the Manager.");
        process = Process.GetProcessById(plan.ParentPid);
        try
        {
            if (
                !Native.GetProcessTimes(process.SafeHandle, out long created, out _, out _, out _)
                || DateTimeOffset.FromFileTime(created).ToUnixTimeMilliseconds()
                    != plan.ParentStartedAtUtc.ToUnixTimeMilliseconds()
                || process.SessionId != self.SessionId
            )
                throw new InvalidDataException("Manager creation receipt changed.");
            var image = new StringBuilder(32768);
            int length = image.Capacity;
            if (
                !Native.QueryFullProcessImageName(process.SafeHandle, 0, image, ref length)
                || !UpdatePaths.Equal(
                    image.ToString(),
                    System.IO.Path.Combine(plan.InstallDirectory, "Bezoro.AhkManager.exe")
                )
            )
                throw new InvalidDataException("Manager image changed.");
            if (!Native.OpenProcessToken(process.SafeHandle, 8, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var parent = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                if (
                    parent.User is null
                    || current.User is null
                    || parent.User.Value != current.User.Value
                )
                    throw new InvalidDataException("Manager user changed.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal bool Exited => process.HasExited;
    internal int ExitCode => process.ExitCode;

    public void Dispose() => process.Dispose();
}
