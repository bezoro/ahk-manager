namespace Bezoro.AhkManager.UpdateHost;

internal static class PortablePromotion
{
    internal const int MaximumAttempts = 6;

    // A sharing violation can come from an indexer, scanner or sync client. Other
    // IO failures and every failed ownership/integrity check still fail closed.
    internal static async Task MoveApplication(
        string source,
        string destination,
        Action revalidate,
        Action<string>? log = null,
        Func<TimeSpan, Task>? delay = null,
        Action<string, string>? move = null
    )
    {
        for (int attempt = 1; ; attempt++)
        {
            revalidate();
            try
            {
                (move ?? Directory.Move)(source, destination);
                if (attempt > 1)
                    log?.Invoke("Portable application move completed after attempt=" + attempt);
                return;
            }
            catch (IOException failure)
                when (SharingViolation(failure) && attempt < MaximumAttempts)
            {
                ApplyDiagnostics.Failure(log, "move-current-application", failure);
                log?.Invoke(
                    "Retrying portable application move after sharing violation attempt=" + attempt
                );
                await (delay ?? Task.Delay)(TimeSpan.FromMilliseconds(40 * (1 << (attempt - 1))))
                    .ConfigureAwait(false);
                // The next iteration checks the entire original snapshot and its
                // security again before any new filesystem mutation is attempted.
            }
        }
    }

    internal static bool SharingViolation(IOException failure) =>
        unchecked((uint)failure.HResult) is 0x80070020 or 0x80070021;
}
