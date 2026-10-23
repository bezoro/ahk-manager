using Microsoft.Windows.ApplicationModel.Resources;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Resources;

internal static class Text
{
    static readonly ResourceLoader Loader = new();
    public static string Get(string key) => Loader.GetString(key);
    public static string Reason(ReasonCode code) => Get("Reason_" + code);
    public static string Status(ScriptStatus status) => Get("Status_" + status);
    public static string Outcome(Outcome outcome) => Get("Outcome_" + outcome);
    public static string Operation(ScriptOperation operation) => Get("Operation_" + operation);
}
