using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Core.Infrastructure;

// Relevance bounds discovery of catalog .ahk scripts. These hints never establish
// script identity, absence of a recognized opaque candidate, or control authority.
internal static class ProcessRelevance
{
    static bool Recognized(string? executable, IReadOnlyCollection<string> configured)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;
        string name = Path.GetFileName(executable);
        return name.Equals("AutoHotkey.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AutoHotkey64.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AutoHotkey32.exe", StringComparison.OrdinalIgnoreCase)
            || configured.Any(path =>
                ScriptPaths.Equal(path, executable)
                || Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)
            );
    }

    internal static bool IsCandidate(
        ProcessCommandRow row,
        IReadOnlyCollection<string> configured,
        string? nativeImage,
        string? nativeProduct
    )
    {
        if (
            Recognized(nativeImage, configured)
            || Recognized(row.Name, configured)
            || Recognized(row.ExecutablePath, configured)
            || nativeProduct?.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase) == true
        )
            return true;
        string[] argv = row.CommandLine is string command ? ScriptPaths.SplitCommand(command) : [];
        return argv.Length > 0 && Recognized(argv[0], configured);
    }

    internal static bool ReadCandidate(
        ProcessCommandRow row,
        IReadOnlyCollection<string> configured,
        NativeProcessIdentity native,
        out string? nativeImage,
        Func<NativeProcessIdentity, string?>? readImage = null
    )
    {
        nativeImage = readImage is null
            ? native.TryReadImage(out var image)
                ? image
                : null
            : readImage(native);
        string? product = null;
        if (nativeImage is not null)
        {
            try
            {
                product = FileVersionInfo.GetVersionInfo(nativeImage).ProductName;
            }
            catch (Exception ex)
                when (SafeJsonFiles.Known(ex)
                    || ex is System.ComponentModel.Win32Exception or InvalidOperationException
                ) { }
        }
        return IsCandidate(row, configured, nativeImage, product);
    }
}
