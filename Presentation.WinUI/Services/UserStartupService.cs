using System.Collections.Immutable;
using System.Management;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed record StartupInspection(bool Complete, ImmutableArray<string> ScriptPaths, ImmutableArray<Issue> Issues);
public interface IUserStartupAdapter
{
    string? ReadEntry();
    bool CompareExchangeEntry(string? expected, string? replacement);
    Task<StartupInspection> InspectAsync(CancellationToken ct);
}

public sealed class RegistryStartupAdapter : IUserStartupAdapter
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string EntryName = "Bezoro.AhkManager.WinUI";
    public string? ReadEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : value is string text ? text : "<non-string>";
    }
    public bool CompareExchangeEntry(string? expected, string? replacement)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        var current = key.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current is not null && current is not string || (string?)current != expected) return false;
        if (replacement is null) key.DeleteValue(EntryName, false);
        else key.SetValue(EntryName, replacement, RegistryValueKind.String);
        return true;
    }
    public Task<StartupInspection> InspectAsync(CancellationToken ct) => Task.Run(() =>
    {
        var paths = ImmutableArray.CreateBuilder<string>(); var issues = ImmutableArray.CreateBuilder<Issue>(); bool complete = true;
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Command FROM Win32_StartupCommand"); search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var rows = search.Get();
            foreach (ManagementObject row in rows) using (row) { ct.ThrowIfCancellationRequested(); if (row["Command"] is string command) Inspect(command); }
            foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.ahk")) { ct.ThrowIfCancellationRequested(); paths.Add(ScriptPaths.Canonical(file)); }
            }
            using var scheduled = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\TaskScheduler"), new ObjectQuery("SELECT Actions FROM MSFT_ScheduledTask"));
            scheduled.Options.Timeout = TimeSpan.FromSeconds(5);
            using var tasks = scheduled.Get();
            foreach (ManagementObject task in tasks)
            using (task)
            {
                ct.ThrowIfCancellationRequested();
                if (task["Actions"] is not Array actions) continue;
                foreach (var value in actions)
                {
                    if (value is not ManagementBaseObject action) { complete = false; continue; }
                    using (action)
                    {
                        if (action.Properties["Execute"]?.Value is not string execute) continue;
                        string arguments = action.Properties["Arguments"]?.Value as string ?? "";
                        Inspect(ScriptPaths.QuoteArgument(execute) + " " + arguments);
                    }
                }
            }
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is ManagementException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        { complete = false; issues.Add(new(ReasonCode.ExternalStartupConflict, ex.Message)); }
        return new StartupInspection(complete, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), issues.ToImmutable());
        void Inspect(string command)
        {
            if (!command.Contains(".ahk", StringComparison.OrdinalIgnoreCase) && !command.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase)) return;
            if (command.Contains('%')) { complete = false; return; }
            var args = ScriptPaths.SplitCommand(command);
            if (args.Length == 1 && args[0].EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) { paths.Add(ScriptPaths.Canonical(args[0])); return; }
            var script = args.Length > 0 ? ScriptPaths.ScriptFromCommand(command, args[0]) : null;
            if (script is null) complete = false; else paths.Add(script);
        }
    }, ct);
}

public sealed class UserStartupService(IUserStartupAdapter adapter, string managerExecutable)
{
    public string Command { get; } = ScriptPaths.QuoteArgument(ScriptPaths.LocalPath(managerExecutable)) + " --login --background";
    public Task<StartupInspection> InspectAsync(CancellationToken ct) => adapter.InspectAsync(ct);
    public ReasonCode RegistrationStatus(bool expectedEnabled)
    {
        try { var existing = adapter.ReadEntry(); return existing == (expectedEnabled ? Command : null) ? ReasonCode.None : existing is null ? ReasonCode.StartupInactive : ReasonCode.StartupEntryConflict; }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { return ReasonCode.AccessDenied; }
    }
    public Task<OperationResult> SetEnabledAsync(bool enabled, bool consent, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string? existing = adapter.ReadEntry();
            if (existing is not null && existing != Command) return new OperationResult(Outcome.Failed, ReasonCode.StartupEntryConflict);
            if (enabled && existing is null && !consent) return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
            string? desired = enabled ? Command : null;
            if (existing == desired) return new(Outcome.Confirmed);
            return adapter.CompareExchangeEntry(existing, desired) ? new(Outcome.Confirmed) : new(Outcome.Unknown, ReasonCode.StartupEntryConflict);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { return new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message); }
    }, ct);
}
