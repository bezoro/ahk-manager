using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static class Evidence
{
    internal static void WriteNew(string path, object value)
    {
        UpdatePaths.NoRedirects(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            value.GetType(),
            UpdateProtocol.Json
        );
        if (bytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Update evidence exceeds limit.");
        using (
            var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )
        )
        {
            file.Write(bytes);
            file.Flush(true);
        }

        File.Move(temporary, path, false);
    }
}
