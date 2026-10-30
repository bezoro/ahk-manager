using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AhkManager.TestSupport;

/// <summary>Holds only the fixture's existing shortcut until its real rename retry is logged.</summary>
public sealed class PortableShortcutSharingProbe : IDisposable
{
    const long MaximumFileBytes = 2 * 1024 * 1024;
    const long MaximumReadBytes = 64 * 1024 * 1024;
    readonly object gate = new();
    readonly CancellationTokenSource cancellation = new();
    readonly Dictionary<string, long> lengths = new(StringComparer.OrdinalIgnoreCase);
    readonly string logDirectory;
    readonly Thread worker;
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    FileStream held;
    bool observed;
    int observedPid;
    int observedAttempt;
    string observedCode = "";
    string failure = "";
    long readBytes;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public bool Observed
    {
        get
        {
            lock (gate)
                return observed;
        }
    }
    public bool Released
    {
        get
        {
            lock (gate)
                return held is null;
        }
    }
    public int ObservedPid
    {
        get
        {
            lock (gate)
                return observedPid;
        }
    }
    public int ObservedAttempt
    {
        get
        {
            lock (gate)
                return observedAttempt;
        }
    }
    public string ObservedCode
    {
        get
        {
            lock (gate)
                return observedCode;
        }
    }
    public string Failure
    {
        get
        {
            lock (gate)
                return failure;
        }
    }

    public PortableShortcutSharingProbe(string shortcut, string logs)
    {
        if (!Path.IsPathFullyQualified(shortcut) || !Path.IsPathFullyQualified(logs))
            throw new ArgumentException("Absolute fixture paths are required.");
        var file = new FileInfo(shortcut);
        var directory = new DirectoryInfo(logs);
        if (
            !file.Exists
            || file.Length <= 0
            || file.Length > 1024 * 1024
            || (file.Attributes & FileAttributes.ReparsePoint) != 0
            || !directory.Exists
            || (directory.Attributes & FileAttributes.ReparsePoint) != 0
        )
            throw new InvalidDataException(
                "The sharing probe requires an existing regular shortcut and local log directory."
            );
        logDirectory = logs;
        // Readers and writers remain permitted. Omitting FileShare.Delete isolates rename contention.
        held = new FileStream(shortcut, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        worker = new Thread(Watch)
        {
            IsBackground = true,
            Name = "Portable shortcut sharing probe",
        };
        try
        {
            worker.Start();
        }
        catch
        {
            held.Dispose();
            held = null;
            throw;
        }
    }

    void Watch()
    {
        try
        {
            while (!cancellation.IsCancellationRequested && elapsed.ElapsedMilliseconds < 10000)
            {
                var files = Directory.GetFiles(
                    logDirectory,
                    "commands-*.jsonl",
                    SearchOption.TopDirectoryOnly
                );
                if (files.Length > 256)
                {
                    SetFailure("file-limit");
                    return;
                }
                foreach (string file in files)
                {
                    if (cancellation.IsCancellationRequested)
                        return;
                    if (Inspect(file))
                        return;
                }
                cancellation.Token.WaitHandle.WaitOne(10);
            }
            if (!cancellation.IsCancellationRequested)
                SetFailure("timeout");
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SetFailure("read-unavailable");
        }
        finally
        {
            Release();
        }
    }

    bool Inspect(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
            return false;
        long length = info.Length;
        if (
            length <= 0
            || length > MaximumFileBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0
        )
            return false;
        if (lengths.TryGetValue(file, out long previous) && previous == length)
            return false;
        lengths[file] = length;
        readBytes += length;
        if (readBytes > MaximumReadBytes)
            throw new IOException("Probe read budget exceeded.");
        byte[] bytes = new byte[(int)length];
        try
        {
            using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = input.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                    return false;
                offset += count;
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        string text = new UTF8Encoding(false, true).GetString(bytes);
        int complete = text.LastIndexOf('\n');
        if (complete < 0)
            return false;
        string[] lines = text[..complete].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return false;
        using var header = JsonDocument.Parse(lines[0]);
        var root = header.RootElement;
        if (
            !String(root, "kind", "session")
            || !String(root, "applicationId", "Bezoro.AhkManager.Electron")
            || !root.TryGetProperty("schemaVersion", out var schema)
            || !schema.TryGetInt32(out int format)
            || format != 1
            || !root.TryGetProperty("pid", out var pidValue)
            || !pidValue.TryGetInt32(out int pid)
            || pid <= 0
            || !root.TryGetProperty("sessionId", out var sessionValue)
            || sessionValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(sessionValue.GetString(), out var session)
            || !Time(root, out var created)
            || created < StartedAtUtc
        )
            return false;
        for (int index = 1; index < lines.Length; index++)
        {
            if (Encoding.UTF8.GetByteCount(lines[index]) > 2048)
                continue;
            using var record = JsonDocument.Parse(lines[index]);
            var value = record.RootElement;
            if (
                !String(value, "kind", "command")
                || !String(value, "source", "lifecycle")
                || !String(value, "command", "repairSearchShortcut.rename")
                || !String(value, "phase", "error")
                || !String(value, "sessionId", session.ToString())
                || !Time(value, out var timestamp)
                || timestamp < StartedAtUtc
                || !value.TryGetProperty("retrying", out var retrying)
                || retrying.ValueKind != JsonValueKind.True
                || !value.TryGetProperty("attempt", out var attemptValue)
                || !attemptValue.TryGetInt32(out int attempt)
                || attempt < 1
                || attempt > 6
                || !value.TryGetProperty("errorCode", out var codeValue)
                || codeValue.ValueKind != JsonValueKind.String
            )
                continue;
            string code = codeValue.GetString();
            if (code is not ("EACCES" or "EPERM" or "EBUSY"))
                continue;
            lock (gate)
            {
                // Release before publishing Observed, so PowerShell can immediately verify repair.
                held?.Dispose();
                held = null;
                observedPid = pid;
                observedAttempt = attempt;
                observedCode = code;
                observed = true;
            }
            return true;
        }
        return false;
    }

    static bool String(JsonElement value, string property, string expected) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var found)
        && found.ValueKind == JsonValueKind.String
        && found.GetString() == expected;

    static bool Time(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value.TryGetProperty("timestamp", out var found)
            && found.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                found.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp
            );
    }

    void SetFailure(string value)
    {
        lock (gate)
            failure = value;
    }

    void Release()
    {
        lock (gate)
        {
            held?.Dispose();
            held = null;
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        Release();
        if (Thread.CurrentThread != worker)
            worker.Join(1000);
    }
}
