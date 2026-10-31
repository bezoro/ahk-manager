namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckPortablePromotionPolicy(Action<bool, string> check)
    {
        foreach (int code in new[] { 32, 33 })
        {
            int attempts = 0,
                validations = 0,
                waits = 0;
            PortablePromotion
                .MoveApplication(
                    "source",
                    "destination",
                    () => validations++,
                    delay: _ =>
                    {
                        waits++;
                        return Task.CompletedTask;
                    },
                    move: (_, _) =>
                    {
                        attempts++;
                        if (attempts < 3)
                            throw new IOException(
                                "locked",
                                unchecked((int)(0x80070000u | (uint)code))
                            );
                    }
                )
                .GetAwaiter()
                .GetResult();
            check(
                attempts == 3 && validations == 3 && waits == 2,
                "portable promotion retries only verified transient sharing failures: " + code
            );
        }
        foreach (int code in new[] { 5, 2, 112, 4390 })
        {
            int attempts = 0,
                waits = 0;
            try
            {
                PortablePromotion
                    .MoveApplication(
                        "source",
                        "destination",
                        () => { },
                        delay: _ =>
                        {
                            waits++;
                            return Task.CompletedTask;
                        },
                        move: (_, _) =>
                        {
                            attempts++;
                            throw new IOException(
                                "other IO failure",
                                unchecked((int)(0x80070000u | (uint)code))
                            );
                        }
                    )
                    .GetAwaiter()
                    .GetResult();
            }
            catch (IOException) { }
            check(
                attempts == 1 && waits == 0,
                "portable promotion refuses nonsharing IO failures without replay: " + code
            );
        }
        int moves = 0,
            checks = 0;
        try
        {
            PortablePromotion
                .MoveApplication(
                    "source",
                    "destination",
                    () =>
                    {
                        if (++checks > 1)
                            throw new InvalidDataException("source changed");
                    },
                    delay: _ => Task.CompletedTask,
                    move: (_, _) =>
                    {
                        moves++;
                        throw new IOException("locked", unchecked((int)0x80070020));
                    }
                )
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidDataException) { }
        check(
            moves == 1 && checks == 2,
            "changed source identity or security prevents a sharing retry before another move"
        );
        int exhausted = 0;
        try
        {
            PortablePromotion
                .MoveApplication(
                    "source",
                    "destination",
                    () => { },
                    delay: _ => Task.CompletedTask,
                    move: (_, _) =>
                    {
                        exhausted++;
                        throw new IOException("locked", unchecked((int)0x80070020));
                    }
                )
                .GetAwaiter()
                .GetResult();
        }
        catch (IOException) { }
        check(
            exhausted == PortablePromotion.MaximumAttempts,
            "portable promotion sharing retries have a finite attempt bound"
        );
        int validationsOnly = 0,
            rejectedMoves = 0;
        try
        {
            PortablePromotion
                .MoveApplication(
                    "source",
                    "destination",
                    () =>
                    {
                        validationsOnly++;
                        throw new IOException(
                            "validation cannot prove the source",
                            unchecked((int)0x80070020)
                        );
                    },
                    delay: _ => Task.CompletedTask,
                    move: (_, _) => rejectedMoves++
                )
                .GetAwaiter()
                .GetResult();
        }
        catch (IOException) { }
        check(
            validationsOnly == 1 && rejectedMoves == 0,
            "sharing failures in source validation are never retried as mutation failures"
        );
    }

    static async Task CheckPortablePromotionSharing(string root, Action<bool, string> check)
    {
        string source = Path.Combine(root, "portable sharing source");
        string destination = Path.Combine(root, "portable sharing previous");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "payload.txt");
        File.WriteAllText(file, "preserved source payload");
        FileStream? hold = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool observed = false;
        int validations = 0;
        try
        {
            await PortablePromotion
                .MoveApplication(
                    source,
                    destination,
                    () =>
                    {
                        validations++;
                        if (
                            File.ReadAllText(file) != "preserved source payload"
                            || Directory.Exists(destination)
                        )
                            throw new InvalidDataException("The isolated source changed.");
                    },
                    message =>
                    {
                        if (
                            message.StartsWith(
                                "Retrying portable application move after sharing violation",
                                StringComparison.Ordinal
                            )
                        )
                        {
                            observed = true;
                            hold.Dispose();
                            hold = null;
                        }
                    }
                )
                .ConfigureAwait(false);
        }
        finally
        {
            hold?.Dispose();
        }
        check(
            observed
                && validations == 2
                && !Directory.Exists(source)
                && File.ReadAllText(Path.Combine(destination, "payload.txt"))
                    == "preserved source payload",
            "real Windows sharing violation on an isolated payload is revalidated and promoted after its external read handle releases"
        );
    }
}
