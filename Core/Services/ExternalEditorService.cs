using System.Diagnostics;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.Core.Services;

public sealed class ExternalEditorService
{
    public Task<OperationResult> OpenAsync(
        string scriptPath,
        ManagerSettings settings,
        bool folder,
        CancellationToken ct = default
    ) =>
        Task.Run(
            () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string script = ScriptPaths.Canonical(scriptPath);
                    if (folder)
                    {
                        using var directory = Process.Start(
                            new ProcessStartInfo(Path.GetDirectoryName(script)!)
                            {
                                UseShellExecute = true,
                            }
                        );
                        return new OperationResult(Outcome.Accepted);
                    }
                    using var self = new NativeProcessIdentity(Environment.ProcessId);
                    if (!self.TryReadSecurity(out var security))
                        return new OperationResult(
                            Outcome.Failed,
                            ReasonCode.EditorUnavailable,
                            "The privilege required to open the editor could not be verified."
                        );
                    bool elevated = security.Elevated || security.IntegrityRid >= 0x3000;
                    string? automaticEditor = string.IsNullOrWhiteSpace(settings.EditorPath)
                        ? DiscoverVisualStudioCode()
                        : null;
                    var info = CreateEditorStartInfo(script, settings, automaticEditor, elevated);
                    try
                    {
                        using var launched = StartEditor(info, elevated);
                        return new OperationResult(Outcome.Accepted);
                    }
                    catch (System.ComponentModel.Win32Exception)
                        when (info.UseShellExecute && info.Verb == "edit")
                    {
                        // The default open association can execute .ahk; only a text editor fallback is safe.
                        var textEditor = new ProcessStartInfo(
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.System),
                                "notepad.exe"
                            )
                        )
                        {
                            UseShellExecute = false,
                            WorkingDirectory = Path.GetDirectoryName(script)!,
                        };
                        textEditor.ArgumentList.Add(script);
                        using var launched = StartEditor(textEditor, elevated);
                        return new OperationResult(Outcome.Accepted);
                    }
                }
                catch (OperationCanceledException)
                {
                    return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
                }
                catch (Exception ex)
                    when (SafeJsonFiles.Known(ex)
                        || ex is System.ComponentModel.Win32Exception or InvalidOperationException
                    )
                {
                    return new(Outcome.Failed, ReasonCode.EditorUnavailable, ex.Message);
                }
            },
            ct
        );

    static Process? StartEditor(ProcessStartInfo info, bool elevated)
    {
        if (!elevated)
            return Process.Start(info);
        if (info.UseShellExecute)
            throw new InvalidOperationException("The editor must run with standard privileges.");
        if (UnelevatedProcessLauncher.UnavailableReason() is not null)
            throw new InvalidOperationException(
                "The editor could not be opened with standard privileges. Check that Windows Explorer is available in this session."
            );
        return UnelevatedProcessLauncher.LaunchExecutable(
                info.FileName,
                info.ArgumentList.ToArray(),
                info.WorkingDirectory
            )
            ?? throw new InvalidOperationException(
                "Windows did not confirm that the editor opened. Check whether it is already open before trying again."
            );
    }

    internal static ProcessStartInfo CreateEditorStartInfo(
        string script,
        ManagerSettings settings,
        string? automaticEditor,
        bool elevated
    )
    {
        script = ScriptPaths.Canonical(script);
        string directory = Path.GetDirectoryName(script)!;
        string? editor = settings.EditorPath is { Length: > 0 } configured
            ? configured
            : automaticEditor;
        if (editor is null)
        {
            if (!elevated)
                return new(script) { UseShellExecute = true, Verb = "edit" };
            var fallback = new ProcessStartInfo(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "notepad.exe"
                )
            )
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };
            fallback.ArgumentList.Add(script);
            return fallback;
        }
        editor = ScriptPaths.Canonical(editor, false);
        if (!editor.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the editor executable.");
        var info = new ProcessStartInfo(editor)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        bool inserted = false;
        foreach (var argument in settings.EditorArguments)
        {
            if (argument.Contains("{file}", StringComparison.Ordinal))
            {
                info.ArgumentList.Add(argument.Replace("{file}", script, StringComparison.Ordinal));
                inserted = true;
            }
            else
                info.ArgumentList.Add(argument);
        }
        if (!inserted)
            info.ArgumentList.Add(script);
        return info;
    }

    internal static string? DiscoverVisualStudioCode(
        IEnumerable<string>? candidates = null,
        Func<string, string?>? validate = null
    )
    {
        foreach (
            string candidate in (candidates ?? VisualStudioCodeCandidates()).Distinct(
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            try
            {
                string? valid = validate is null
                    ? ValidateVisualStudioCode(candidate)
                    : validate(candidate);
                if (valid is not null)
                    return valid;
            }
            catch (Exception ex)
                when (SafeJsonFiles.Known(ex)
                    || ex is System.ComponentModel.Win32Exception or InvalidOperationException
                ) { }
        }
        return null;
    }

    static string? ValidateVisualStudioCode(string candidate)
    {
        if (!Path.GetFileName(candidate).Equals("Code.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        string executable = ScriptPaths.Canonical(candidate, false);
        string? product = FileVersionInfo.GetVersionInfo(executable).ProductName;
        return
            product is not null
            && product.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
            ? executable
            : null;
    }

    static IEnumerable<string> VisualStudioCodeCandidates()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\Code.exe"
                );
                if (key?.GetValue("") is string path)
                    candidates.Add(path.Trim().Trim('"'));
            }
            catch (Exception ex)
                when (ex
                        is UnauthorizedAccessException
                            or System.Security.SecurityException
                            or IOException
                ) { }
        }
        foreach (
            string root in new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs"
                ),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
        )
            if (!string.IsNullOrEmpty(root))
                candidates.Add(Path.Combine(root, "Microsoft VS Code", "Code.exe"));
        foreach (
            string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(directory))
                continue;
            candidates.Add(Path.Combine(directory, "Code.exe"));
            if (
                Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(directory) is { } parent
            )
                candidates.Add(Path.Combine(parent, "Code.exe"));
        }
        return candidates;
    }
}
