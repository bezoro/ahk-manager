using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed record InterpreterInfo(string Path, string Version, string Hash);

public sealed class AutoHotkeyDiscovery
{
    public static InterpreterInfo Validate(string path)
    {
        var canonical = ScriptPaths.Canonical(path, false);
        if (!canonical.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Executable required.");
        var info = FileVersionInfo.GetVersionInfo(canonical);
        if (info.FileMajorPart != 2 || info.ProductName is null || !info.ProductName.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("AutoHotkey v2 product/version required.");
        return new(canonical, new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart).ToString(), ScriptPaths.Fingerprint(canonical));
    }
    public Task<ImmutableArray<InterpreterInfo>> DiscoverAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var keyName in new[] { @"Software\Microsoft\Windows\CurrentVersion\App Paths\AutoHotkey.exe", @"Software\AutoHotkey", @"Software\WOW6432Node\AutoHotkey" })
        {
            try
            {
                using var key = hive.OpenSubKey(keyName);
                if (key?.GetValue("") is string executable && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) candidates.Add(executable.Trim('"'));
                if (key?.GetValue("InstallDir") is string directory) AddDirectory(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (var basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
            AddDirectory(Path.Combine(basePath, "AutoHotkey"));
        var found = ImmutableArray.CreateBuilder<InterpreterInfo>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try { found.Add(Validate(candidate)); }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { }
        }
        return found.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        void AddDirectory(string directory)
        {
            foreach (var file in new[] { "AutoHotkey.exe", "AutoHotkey64.exe", "AutoHotkey32.exe", @"v2\AutoHotkey64.exe", @"v2\AutoHotkey32.exe" }) candidates.Add(Path.Combine(directory, file));
        }
    }, ct);

    public static async Task ValidateScriptVersionAsync(string script, InterpreterInfo interpreter, CancellationToken ct)
    {
        var bytes = await SafeJsonFiles.ReadAsync(script, ct).ConfigureAwait(false) ?? throw new FileNotFoundException();
        var text = Decode(bytes);
        if (!Version.TryParse(interpreter.Version, out var actual) || actual.Major != 2) throw new InvalidDataException("AutoHotkey v2 version required.");
        bool blockComment = false, continuation = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (continuation) { if (trimmed.StartsWith(')')) continuation = false; continue; }
            if (blockComment) { if (trimmed.StartsWith("*/", StringComparison.Ordinal)) blockComment = false; continue; }
            if (trimmed.StartsWith("/*", StringComparison.Ordinal)) { blockComment = !trimmed.Contains("*/", StringComparison.Ordinal); continue; }
            if (IsContinuationStart(trimmed)) { continuation = true; continue; }
            if (!trimmed.StartsWith("#Requires", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].Equals("AutoHotkey", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Script requires an incompatible interpreter.");
            string declared = parts[2].TrimStart('v', 'V').TrimEnd('+');
            if (declared == "2") declared = "2.0";
            if (!Version.TryParse(declared, out var minimum) || minimum.Major != 2 || actual < minimum) throw new InvalidDataException("Script requires an incompatible interpreter.");
        }
    }
    static bool IsContinuationStart(string line)
    {
        if (!line.StartsWith('(')) return false;
        foreach (string option in line[1..].Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (option.StartsWith(';')) break;
            if (!option.StartsWith("Join", StringComparison.OrdinalIgnoreCase) && (option.Contains('(') || option.Contains(')'))) return false;
        }
        return true;
    }
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 4 && (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0 || bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)) throw new InvalidDataException("UTF32 unsupported.");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }
}
