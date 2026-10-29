using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Core.Infrastructure;

public static class ScriptPaths
{
    public static bool Equal(string? left, string? right) =>
        left is not null
        && right is not null
        && StringComparer.OrdinalIgnoreCase.Equals(left, right);

    public static string Lexical(string path)
    {
        var full = LocalPath(path);
        if (!full.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AHK file required.");
        return full;
    }

    public static string LocalPath(string path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.Length > 32760
            || path.Contains('"')
            || path.Any(c => c < 32 || "<>|*?".Contains(c))
        )
            throw new ArgumentException("Invalid absolute path.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.AsSpan(2).Contains(':'))
            throw new ArgumentException("Local drive required.");
        return full;
    }

    public static string Canonical(string path, bool script = true)
    {
        var full = script ? Lexical(path) : LocalPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if (
                (File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException("Redirected path refused.");
        if (!File.Exists(full))
            throw new FileNotFoundException("File absent.", full);
        StringBuilder buffer = new(32768);
        if (ProcessNative.GetLongPathName(full, buffer, (uint)buffer.Capacity) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        full = buffer.ToString();
        using var handle = File.OpenHandle(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        if (!ProcessNative.GetFileInformationByHandle(handle, out var stamp) || stamp.Links != 1)
            throw new IOException("Uncertain or multiply linked file identity.");
        return full;
    }

    public static string Fingerprint(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024 * 1024)
            throw new IOException("File exceeds fingerprint limit.");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static ScriptFileIdentity FileIdentity(string path)
    {
        string canonical = Canonical(path);
        using var stream = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        if (
            stream.Length > 64 * 1024 * 1024
            || !ProcessNative.GetFileInformationByHandle(stream.SafeFileHandle, out var stamp)
            || stamp.Links != 1
        )
            throw new IOException("Uncertain file identity.");
        string id =
            $"{stamp.Volume}:{stamp.IndexHigh}:{stamp.IndexLow}:{stamp.Creation}:{stamp.Write}:{stamp.SizeHigh}:{stamp.SizeLow}";
        return new(canonical, id, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    public static string[] SplitCommand(string command)
    {
        if (
            string.IsNullOrWhiteSpace(command)
            || command.Length > 32768
            || command.Contains('\0')
            || char.IsWhiteSpace(command[0])
        )
            return [];
        bool quoted = false;
        int slashes = 0;
        foreach (char c in command)
        {
            if (c == '"' && slashes % 2 == 0)
                quoted = !quoted;
            slashes = c == '\\' ? slashes + 1 : 0;
        }
        if (quoted)
            return [];
        nint pointer = ProcessNative.CommandLineToArgvW(command, out int count);
        if (pointer == 0 || count is < 1 or > 256)
        {
            if (pointer != 0)
                ProcessNative.LocalFree(pointer);
            return [];
        }
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++)
                args[i] =
                    Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally
        {
            ProcessNative.LocalFree(pointer);
        }
    }

    public static string? ScriptFromCommand(string command, string executable)
    {
        var args = SplitCommand(command);
        if (args.Length < 2 || !Equal(args[0], executable))
            return null;
        int index = args[1].Equals("/script", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (
            index >= args.Length
            || args[index].StartsWith('/')
            || !Path.IsPathFullyQualified(args[index])
        )
            return null;
        try
        {
            return Canonical(args[index]);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or Win32Exception
            )
        {
            return null;
        }
    }

    // Used only for shell APIs which require one command line; normal launch uses ArgumentList.
    public static string QuoteArgument(string value)
    {
        StringBuilder text = new("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            if (c == '"')
                text.Append('\\', slashes * 2 + 1).Append(c);
            else
                text.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}
