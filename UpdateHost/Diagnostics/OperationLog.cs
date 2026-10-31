using System.Text;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class OperationLog : IDisposable
{
    readonly StreamWriter output;
    int bytes;
    bool unavailable;
    bool limitReported;
    internal const int MaximumBytes = 32768;
    const int ReservedLimitBytes = 256;

    internal OperationLog(string directory)
    {
        string path = Path.Combine(directory, "helper.log");
        UpdatePaths.NoRedirects(path);
        output = new(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)
        )
        {
            AutoFlush = true,
        };
    }

    internal void Write(string text)
    {
        if (unavailable || limitReported)
            return;
        string line = DateTimeOffset.UtcNow.ToString("O") + " " + BoundedMessage(text);
        if (bytes + Encoding.UTF8.GetByteCount(line) + 2 > MaximumBytes - ReservedLimitBytes)
        {
            line =
                DateTimeOffset.UtcNow.ToString("O")
                + " [helper.log size limit reached; additional entries omitted]";
            limitReported = true;
        }
        try
        {
            output.WriteLine(line);
            bytes += Encoding.UTF8.GetByteCount(line) + 2;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            unavailable = true;
        }
    }

    internal static string BoundedMessage(string text)
    {
        if (text.Length <= 2000)
            return text;
        string suffix = " [message truncated; original characters=" + text.Length + "]";
        return text[..(2000 - suffix.Length)] + suffix;
    }

    public void Dispose()
    {
        try
        {
            output.Dispose();
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}
