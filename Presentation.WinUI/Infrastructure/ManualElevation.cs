using System.ComponentModel;
using System.Diagnostics;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

public sealed record ElevationDispatch(Process? Process, OperationResult Result);
public interface IManualElevationDispatcher
{
    ElevationDispatch Dispatch(string interpreter, string script);
}

public sealed class ManualElevation : IManualElevationDispatcher
{
    public ElevationDispatch Dispatch(string interpreter, string script)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(interpreter)
            {
                UseShellExecute = true, Verb = "runas", Arguments = ScriptPaths.QuoteArgument(script),
                WorkingDirectory = Path.GetDirectoryName(script)!
            });
            return new(process, new(Outcome.Pending, ReasonCode.LaunchUncertain));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(null, new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled, ex.Message)); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { return new(null, new(Outcome.Unknown, ReasonCode.LaunchUncertain, ex.Message)); }
    }
}
