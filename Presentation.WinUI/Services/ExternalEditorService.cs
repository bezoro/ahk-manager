using System.Diagnostics;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed class ExternalEditorService
{
    public Task<OperationResult> OpenAsync(string scriptPath, ManagerSettings settings, bool folder, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string script = ScriptPaths.Canonical(scriptPath);
            ProcessStartInfo info;
            if (folder) info = new(Path.GetDirectoryName(script)!) { UseShellExecute = true };
            else if (settings.EditorPath is { Length: > 0 } editor)
            {
                editor = ScriptPaths.Canonical(editor, false);
                if (!editor.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new OperationResult(Outcome.Failed, ReasonCode.EditorUnavailable);
                info = new(editor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(script)! };
                bool inserted = false;
                foreach (var argument in settings.EditorArguments)
                {
                    if (argument.Contains("{file}", StringComparison.Ordinal)) { info.ArgumentList.Add(argument.Replace("{file}", script, StringComparison.Ordinal)); inserted = true; }
                    else info.ArgumentList.Add(argument);
                }
                if (!inserted) info.ArgumentList.Add(script);
            }
            else info = new(script) { UseShellExecute = true, Verb = "edit" };
            try { using var launched = Process.Start(info); return new OperationResult(Outcome.Accepted); }
            catch (System.ComponentModel.Win32Exception) when (!folder && settings.EditorPath is null)
            {
                // The default open association can execute .ahk; only a text editor fallback is safe.
                var textEditor = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")) { UseShellExecute = false };
                textEditor.ArgumentList.Add(script); using var launched = Process.Start(textEditor);
                return new OperationResult(Outcome.Accepted);
            }
        }
        catch (OperationCanceledException) { return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(Outcome.Failed, ReasonCode.EditorUnavailable, ex.Message); }
    }, ct);
}
