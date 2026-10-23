using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed class ActivityStore(string directory, int maximumBytes = 1024 * 1024, int rotations = 3)
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly ManagerJson compact = new(new JsonSerializerOptions { WriteIndented = false });
    string FilePath => Path.Combine(directory, "activity.jsonl");
    public async Task<OperationResult> AppendAsync(ActivitySnapshot item, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SafeJsonFiles.SafePath(FilePath); Directory.CreateDirectory(directory);
            var bounded = item with { Detail = item.Detail is { Length: > 16000 } detail ? detail[..16000] : item.Detail };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, compact.ActivitySnapshot);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length + bytes.Length + 1 > maximumBytes)
            {
                for (int i = Math.Clamp(rotations, 1, 10) - 1; i >= 1; i--)
                {
                    var previous = FilePath + "." + i;
                    if (File.Exists(previous)) { SafeJsonFiles.SafePath(previous); SafeJsonFiles.SafePath(FilePath + "." + (i + 1)); File.Move(previous, FilePath + "." + (i + 1), true); }
                }
                SafeJsonFiles.SafePath(FilePath + ".1"); File.Move(FilePath, FilePath + ".1", true);
            }
            await using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.WriteAsync(new byte[] { 10 }, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false);
            return new(Outcome.Confirmed);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { return new(Outcome.Failed, ReasonCode.ActivityUnavailable, ex.Message); }
        finally { gate.Release(); }
    }
    public async Task<ImmutableArray<ActivitySnapshot>> ReadAsync(ActivityQuery query, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = new List<ActivitySnapshot>();
            foreach (var path in Enumerable.Range(0, Math.Clamp(rotations, 1, 10) + 1).Select(i => i == 0 ? FilePath : FilePath + "." + i))
            {
                SafeJsonFiles.SafePath(path);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > Math.Max(maximumBytes, 100000) + 100000) throw new InvalidDataException("Activity exceeds limit.");
                using var reader = new StreamReader(path, new UTF8Encoding(false, true));
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (line.Length > 100000) continue;
                    try
                    {
                        var item = JsonSerializer.Deserialize(line, ManagerJson.Default.ActivitySnapshot);
                        if (item is not null && (!query.ScriptId.HasValue || query.ScriptId == item.ScriptId) && (!query.Outcome.HasValue || query.Outcome == item.Outcome) && (query.Search.Length == 0 || (item.Code + " " + item.Detail).Contains(query.Search, StringComparison.CurrentCultureIgnoreCase))) items.Add(item);
                    }
                    catch (JsonException) { /* A truncated last line is not an invented event. */ }
                }
            }
            return items.OrderByDescending(a => a.Timestamp).Take(2000).ToImmutableArray();
        }
        finally { gate.Release(); }
    }
}
