using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.Core.Services;

public sealed record InterpreterInfo(string Path, string Version, string Hash)
{
    public string? ProductVersion { get; init; }
    public int? Bitness { get; init; }
}

public sealed class AutoHotkeyDiscovery
{
    public static InterpreterInfo Validate(string path)
    {
        var canonical = ScriptPaths.Canonical(path, false);
        if (!canonical.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Executable required.");
        var info = FileVersionInfo.GetVersionInfo(canonical);
        if (
            info.FileMajorPart != 2
            || info.ProductName is null
            || !info.ProductName.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException("AutoHotkey v2 product/version required.");
        if (!AhkVersion.TryParse(info.ProductVersion, out var product) || product!.Major != 2)
            throw new InvalidDataException("AutoHotkey v2 product version required.");
        return new(
            canonical,
            new Version(
                info.FileMajorPart,
                info.FileMinorPart,
                info.FileBuildPart,
                info.FilePrivatePart
            ).ToString(),
            ScriptPaths.Fingerprint(canonical)
        )
        {
            ProductVersion = info.ProductVersion,
            Bitness = ReadExecutableBitness(canonical),
        };
    }

    public Task<ImmutableArray<InterpreterInfo>> DiscoverAsync(CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
                foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (
                    var keyName in new[]
                    {
                        @"Software\Microsoft\Windows\CurrentVersion\App Paths\AutoHotkey.exe",
                        @"Software\AutoHotkey",
                        @"Software\WOW6432Node\AutoHotkey",
                    }
                )
                {
                    try
                    {
                        using var key = hive.OpenSubKey(keyName);
                        if (
                            key?.GetValue("") is string executable
                            && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        )
                            candidates.Add(executable.Trim('"'));
                        if (key?.GetValue("InstallDir") is string directory)
                            AddDirectory(directory);
                    }
                    catch (Exception ex)
                        when (ex is UnauthorizedAccessException or System.Security.SecurityException
                        ) { }
                }
                foreach (
                    var basePath in new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.LocalApplicationData
                            ),
                            "Programs"
                        ),
                    }
                )
                    AddDirectory(Path.Combine(basePath, "AutoHotkey"));
                var found = ImmutableArray.CreateBuilder<InterpreterInfo>();
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        found.Add(Validate(candidate));
                    }
                    catch (Exception ex)
                        when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception)
                    { }
                }
                return found
                    .DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();
                void AddDirectory(string directory)
                {
                    foreach (
                        var file in new[]
                        {
                            "AutoHotkey.exe",
                            "AutoHotkey64.exe",
                            "AutoHotkey32.exe",
                            @"v2\AutoHotkey64.exe",
                            @"v2\AutoHotkey32.exe",
                        }
                    )
                        candidates.Add(Path.Combine(directory, file));
                }
            },
            ct
        );

    public static async Task ValidateScriptVersionAsync(
        string script,
        InterpreterInfo interpreter,
        CancellationToken ct
    )
    {
        var bytes =
            await SafeJsonFiles.ReadAsync(script, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException();
        var text = Decode(bytes);
        if (
            !AhkVersion.TryParse(interpreter.ProductVersion ?? interpreter.Version, out var actual)
            || actual!.Major != 2
        )
            throw new InvalidDataException("AutoHotkey v2 version required.");
        bool blockComment = false,
            continuation = false;
        foreach (var line in text.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (continuation)
            {
                if (trimmed.StartsWith(')'))
                    continuation = false;
                continue;
            }
            if (blockComment)
            {
                if (trimmed.StartsWith("*/", StringComparison.Ordinal))
                    blockComment = false;
                continue;
            }
            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                blockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }
            if (IsContinuationStart(trimmed))
            {
                continuation = true;
                continue;
            }
            if (
                !trimmed.StartsWith("#Requires", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > 9 && !char.IsWhiteSpace(trimmed[9]))
            )
                continue;
            for (int i = 1; i < trimmed.Length; i++)
                if (trimmed[i] == ';' && char.IsWhiteSpace(trimmed[i - 1]))
                {
                    trimmed = trimmed[..i];
                    break;
                }
            var parts = trimmed.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (
                parts.Length < 2
                || !parts[1].Equals("AutoHotkey", StringComparison.OrdinalIgnoreCase)
            )
                throw Incompatible();
            foreach (string requirement in parts.Skip(2))
            {
                if (requirement is "32-bit" or "64-bit")
                {
                    int bitness = interpreter.Bitness ?? ReadExecutableBitness(interpreter.Path);
                    if (bitness != (requirement == "32-bit" ? 32 : 64))
                        throw Incompatible();
                    continue;
                }
                int prefix =
                    requirement.StartsWith("<=", StringComparison.Ordinal)
                    || requirement.StartsWith(">=", StringComparison.Ordinal)
                        ? 2
                    : requirement[0] is '<' or '>' or '=' ? 1
                    : 0;
                string op = requirement[..prefix];
                if (!AhkVersion.TryParse(requirement[prefix..], out var required))
                    throw Incompatible();
                int comparison = actual.CompareTo(required!);
                bool matches = op switch
                {
                    "<" => comparison < 0,
                    "<=" => comparison <= 0,
                    ">" => comparison > 0,
                    ">=" => comparison >= 0,
                    "=" => comparison == 0,
                    _ => comparison >= 0 && actual.Major == required!.Major,
                };
                if (!matches)
                    throw Incompatible();
            }
        }
    }

    static InvalidDataException Incompatible() =>
        new("Script requires an incompatible interpreter.");

    // Read only PE metadata. Neither the interpreter nor its code is loaded or executed.
    internal static int ReadExecutableBitness(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
            throw Incompatible();
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 26)
            throw Incompatible();
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550)
            throw Incompatible();
        ushort machine = reader.ReadUInt16();
        stream.Position = offset + 20;
        ushort optionalSize = reader.ReadUInt16();
        if (optionalSize < 2 || offset + 24L + optionalSize > stream.Length)
            throw Incompatible();
        stream.Position = offset + 24;
        ushort magic = reader.ReadUInt16();
        return (machine, magic) switch
        {
            (0x14c, 0x10b) => 32,
            (0x8664, 0x20b) => 64,
            (0xaa64, 0x20b) => 64,
            _ => throw Incompatible(),
        };
    }

    sealed record AhkVersion(string[] Components, string[]? Prerelease) : IComparable<AhkVersion>
    {
        public BigInteger Major => BigInteger.Parse(Components[0]);

        public static bool TryParse(string? value, out AhkVersion? version)
        {
            version = null;
            if (string.IsNullOrEmpty(value))
                return false;
            value = value.Split('+')[0];
            if (value.StartsWith('v') || value.StartsWith('V'))
                value = value[1..];
            if (
                !Regex.IsMatch(
                    value,
                    @"^[0-9]+(?:\.[a-zA-Z0-9]+)*(?:-[a-zA-Z0-9-]*(?:\.[a-zA-Z0-9-]+)*)?$",
                    RegexOptions.CultureInvariant
                )
            )
                return false;
            string[] parts = value.Split('-', 2);
            version = new(
                parts[0].Split('.'),
                parts.Length == 2 ? (parts[1].Length == 0 ? ["0"] : parts[1].Split('.')) : null
            );
            return true;
        }

        public int CompareTo(AhkVersion? other)
        {
            if (other is null)
                return 1;
            int result = Compare(Components, other.Components);
            if (result != 0)
                return result;
            if (Prerelease is null)
                return other.Prerelease is null ? 0 : 1;
            return other.Prerelease is null ? -1 : Compare(Prerelease, other.Prerelease);
        }

        static int Compare(string[] left, string[] right)
        {
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                string a = i < left.Length ? left[i] : "0",
                    b = i < right.Length ? right[i] : "0";
                bool an = BigInteger.TryParse(a, out var av),
                    bn = BigInteger.TryParse(b, out var bv);
                int result =
                    an && bn ? av.CompareTo(bv)
                    : an != bn ? (an ? -1 : 1)
                    : StringComparer.Ordinal.Compare(a, b);
                if (result != 0)
                    return result;
            }
            return 0;
        }
    }

    static bool IsContinuationStart(string line)
    {
        if (!line.StartsWith('('))
            return false;
        foreach (
            string option in line[1..]
                .Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries)
        )
        {
            if (option.StartsWith(';'))
                break;
            if (
                !option.StartsWith("Join", StringComparison.OrdinalIgnoreCase)
                && (option.Contains('(') || option.Contains(')'))
            )
                return false;
        }
        return true;
    }

    internal static string Decode(byte[] bytes)
    {
        if (
            bytes.Length >= 4
            && (
                bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0
                || bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF
            )
        )
            throw new InvalidDataException("UTF32 unsupported.");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }
}
