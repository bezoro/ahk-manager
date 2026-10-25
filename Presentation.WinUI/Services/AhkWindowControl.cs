using System.Text;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public static class AhkWindowControl
{
    // Official AHK v2.0.30 source: WM_CLOSE -> ExitApp(EXIT_CLOSE); OnExit may veto.
    // WM_SYSCOMMAND/SC_CLOSE hides the main window and is deliberately not used.
    public static nint FindWindow(ProcessIdentity identity)
    {
        nint result = 0; bool ambiguous = false;
        ProcessNative.EnumWindows((window, _) =>
        {
            if (!VerifyWindow(window, identity)) return true;
            if (result != 0) ambiguous = true;
            result = window; return true;
        }, 0);
        return ambiguous ? 0 : result;
    }
    public static bool VerifyWindow(nint window, ProcessIdentity identity)
    {
        if (window == 0 || ProcessNative.GetWindowThreadProcessId(window, out uint pid) == 0 || pid != identity.Pid) return false;
        StringBuilder kind = new(256);
        // Script identity was already proved independently by argv or a complete launch receipt.
        // The title is mutable and must neither invalidate B nor assign its window to A.
        return ProcessNative.GetClassName(window, kind, kind.Capacity) != 0 && kind.ToString() == "AutoHotkey";
    }
    public static OperationResult Request(NativeProcessIdentity held, ProcessIdentity expected, ScriptOperation operation)
    {
        if (!held.Matches(expected)) return new(Outcome.Failed, ReasonCode.IdentityChanged);
        if (held.ControlUnavailableReason() is ReasonCode denied) return new(Outcome.Failed, denied);
        nint window = FindWindow(expected);
        if (window == 0 || !VerifyWindow(window, expected) || !held.Matches(expected)) return new(Outcome.Failed, ReasonCode.WindowUnverified);
        uint message; nuint argument;
        switch (operation)
        {
            case ScriptOperation.Stop: message = 0x0010; argument = 0; break;
            case ScriptOperation.TogglePause: message = 0x0111; argument = 65403; break;
            case ScriptOperation.ToggleSuspend: message = 0x0111; argument = 65404; break;
            default: return new(Outcome.Failed, ReasonCode.CapabilityUnverified);
        }
        // SMTO_BLOCK | SMTO_ABORTIFHUNG: dispatch is bounded, and never establishes effect.
        nint dispatched = ProcessNative.SendMessageTimeout(window, message, argument, 0, 3, 1000, out _);
        return dispatched == 0 ? new(Outcome.Unknown, ReasonCode.DispatchFailed) : new(Outcome.Pending, operation == ScriptOperation.Stop ? ReasonCode.ExitNotObserved : ReasonCode.StateNotObservable);
    }
    public static bool? ReadSuspend(ProcessIdentity identity)
    {
        nint window = FindWindow(identity);
        if (window == 0) return null;
        nint menu = ProcessNative.GetMenu(window);
        if (menu == 0) return null;
        uint state = ProcessNative.GetMenuState(menu, 65404, 0);
        return state == uint.MaxValue ? null : (state & 8) != 0;
    }
}
