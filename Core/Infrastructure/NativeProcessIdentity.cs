using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Core.Infrastructure;

public readonly record struct ProcessSecurity(bool Elevated, uint IntegrityRid);

public readonly record struct ProcessStamp(
    int Pid,
    long Created,
    string Executable,
    string Sid,
    int Session
);

// The process handle stays open through argv acquisition, HWND proof, dispatch and exit observation.
public sealed class NativeProcessIdentity : IDisposable
{
    readonly SafeProcessHandle handle;
    public int Pid { get; }

    public NativeProcessIdentity(int pid)
    {
        Pid = pid;
        handle = ProcessNative.OpenProcess(ProcessNative.QuerySynchronize, false, pid);
    }

    public bool Live => !handle.IsInvalid && ProcessNative.WaitForSingleObject(handle, 0) == 258;
    public bool Exited => !handle.IsInvalid && ProcessNative.WaitForSingleObject(handle, 0) == 0;

    internal bool TryReadImage(out string image)
    {
        image = "";
        if (!Live)
            return false;
        StringBuilder text = new(32768);
        int capacity = text.Capacity;
        if (!ProcessNative.QueryFullProcessImageName(handle, 0, text, ref capacity) || !Live)
            return false;
        image = text.ToString();
        return image.Length > 0;
    }

    public bool TryRead(out ProcessStamp stamp)
    {
        stamp = default;
        if (!Live || !ProcessNative.GetProcessTimes(handle, out var created, out _, out _, out _))
            return false;
        StringBuilder text = new(32768);
        int capacity = text.Capacity;
        if (
            !ProcessNative.QueryFullProcessImageName(handle, 0, text, ref capacity)
            || !ProcessNative.OpenProcessToken(handle, 8, out var token)
        )
            return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (
                !ProcessNative.GetTokenInformation(
                    token,
                    12,
                    out int session,
                    sizeof(int),
                    out int returned
                )
                || returned != sizeof(int)
            )
                return false;
            stamp = new(
                (int)ProcessNative.GetProcessId(handle),
                created,
                text.ToString(),
                identity.User?.Value ?? "",
                session
            );
            return stamp.Pid == Pid && stamp.Created > 0 && stamp.Sid.Length > 0;
        }
    }

    public bool TryReadSecurity(out ProcessSecurity security)
    {
        security = default;
        if (!Live || !ProcessNative.OpenProcessToken(handle, 8, out var token))
            return false;
        using (token)
        {
            if (
                !ProcessNative.GetTokenInformation(
                    token,
                    20,
                    out int elevated,
                    sizeof(int),
                    out int returned
                )
                || returned != sizeof(int)
            )
                return false;
            _ = ProcessNative.GetTokenInformation(token, 25, 0, 0, out int length);
            if (length is < 8 or > 4096)
                return false;
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!ProcessNative.GetTokenInformation(token, 25, buffer, length, out _))
                    return false;
                nint sid = Marshal.ReadIntPtr(buffer),
                    count = ProcessNative.GetSidSubAuthorityCount(sid);
                if (count == 0 || Marshal.ReadByte(count) == 0)
                    return false;
                uint level = unchecked(
                    (uint)
                        Marshal.ReadInt32(
                            ProcessNative.GetSidSubAuthority(
                                sid,
                                (uint)(Marshal.ReadByte(count) - 1)
                            )
                        )
                );
                security = new(elevated != 0, level);
                return Live;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public ReasonCode? ControlUnavailableReason()
    {
        using var self = new NativeProcessIdentity(Environment.ProcessId);
        if (!TryReadSecurity(out var target) || !self.TryReadSecurity(out var current))
            return ReasonCode.AccessDenied;
        return target.IntegrityRid > current.IntegrityRid
            ? ReasonCode.ManualElevationRequired
            : null;
    }

    public static bool Same(ProcessIdentity? left, ProcessIdentity? right) =>
        left is not null
        && right is not null
        && left.Pid == right.Pid
        && left.CreationFileTime == right.CreationFileTime
        && ScriptPaths.Equal(left.ExecutablePath, right.ExecutablePath)
        && ScriptPaths.Equal(left.ScriptPath, right.ScriptPath)
        && left.UserSid == right.UserSid
        && left.SessionId == right.SessionId
        && left.ScriptHash == right.ScriptHash
        && left.InterpreterHash == right.InterpreterHash;

    public bool Matches(ProcessIdentity expected) =>
        TryRead(out var stamp)
        && stamp.Pid == expected.Pid
        && stamp.Created == expected.CreationFileTime
        && ScriptPaths.Equal(stamp.Executable, expected.ExecutablePath)
        && stamp.Sid == expected.UserSid
        && stamp.Session == expected.SessionId;

    public async Task<bool> WaitForExitAsync(TimeSpan deadline)
    {
        // Caller cancellation cannot turn an already delivered request into no effect.
        return await Task.Run(() =>
                ProcessNative.WaitForSingleObject(handle, checked((uint)deadline.TotalMilliseconds))
                == 0
            )
            .ConfigureAwait(false);
    }

    public void Dispose() => handle.Dispose();
}
