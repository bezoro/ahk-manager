using System.Text;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class OperationLog : IDisposable
{
    readonly StreamWriter output;
    int bytes;
    bool unavailable;

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
        if (unavailable)
            return;
        string line =
            DateTimeOffset.UtcNow.ToString("O") + " " + text[..Math.Min(text.Length, 2000)];
        if (bytes + Encoding.UTF8.GetByteCount(line) > 32768)
            return;
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
