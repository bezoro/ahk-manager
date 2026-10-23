using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

public interface IEffectOwner : IDisposable
{
    bool Held { get; }
    bool Trusted { get; }
    string Sid { get; }
    string LoginKey { get; }
    bool Allows(string scriptPath, string interpreterPath);
}

public sealed class UserEffectOwner : IEffectOwner
{
    readonly ManualResetEventSlim stop = new();
    readonly ManualResetEventSlim ready = new();
    readonly Thread thread;
    public bool Held { get; private set; }
    public bool Trusted { get; private set; }
    public string Sid { get; private set; } = "";
    public string LoginKey { get; private set; } = "";
    public UserEffectOwner()
    {
        // A named Mutex is thread-affine; acquisition and release must share this dedicated thread.
        thread = new Thread(Own) { IsBackground = true, Name = "AHK Manager effect owner" };
        thread.Start(); ready.Wait();
    }
    void Own()
    {
        Mutex? mutex = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            Sid = identity.User?.Value ?? "";
            if (Sid.Length == 0 || !ProcessNative.GetTokenInformation(identity.AccessToken, 10, out ProcessNative.TokenStatistics statistics, Marshal.SizeOf<ProcessNative.TokenStatistics>(), out _) || statistics.AuthenticationId.Low == 0 && statistics.AuthenticationId.High == 0) return;
            LoginKey = Sid + "|" + statistics.AuthenticationId.High.ToString("X8") + statistics.AuthenticationId.Low.ToString("X8");
            Trusted = true;
            mutex = new Mutex(false, "Global\\Bezoro.AhkManager.Effects." + Sid);
            try { Held = mutex.WaitOne(0); } catch (AbandonedMutexException) { Held = true; }
            ready.Set();
            if (Held) stop.Wait();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception or IOException) { Trusted = false; }
        finally { ready.Set(); if (Held) { mutex!.ReleaseMutex(); Held = false; } mutex?.Dispose(); }
    }
    public bool Allows(string scriptPath, string interpreterPath) => Held && Trusted;
    public void Dispose() { stop.Set(); if (Thread.CurrentThread != thread) thread.Join(); stop.Dispose(); ready.Dispose(); }
}
