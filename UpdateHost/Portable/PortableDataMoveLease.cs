using System.Globalization;
using System.Text;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed record PortableDirectoryIdentity(string Dev, string Ino, string BirthtimeNs);

internal sealed record PortableDataMove(
    int SchemaVersion,
    string OperationId,
    string SourceDataDirectory,
    string DestinationDataDirectory,
    string StagingDirectory,
    string CreatedAtUtc,
    PortableDirectoryIdentity SourceIdentity,
    PortableDirectoryIdentity StageIdentity,
    PortableDirectoryIdentity? DestinationIdentity
);

// Read-only namespace retention for the authenticated parent's startup migration.
// The marker and pending intent determine every path; stdin carries no paths.
internal sealed class PortableDataMoveLease : IDisposable
{
    readonly DirectoryLease ancestors;
    DirectoryLease? stage;

    PortableDataMoveLease(DirectoryLease ancestors, DirectoryLease stage)
    {
        this.ancestors = ancestors;
        this.stage = stage;
    }

    internal static PortableDataMoveLease Acquire(PortableLayout layout)
    {
        ProfileOwnership.Verify(layout.Root);
        var pending = ReadPending(layout);
        bool committed = UpdatePaths.Equal(layout.Data, pending.DestinationDataDirectory);
        var ancestors = new DirectoryLease(
            new[] { layout.Root, Path.GetDirectoryName(pending.DestinationDataDirectory)! }.Concat(
                committed ? [] : new[] { pending.SourceDataDirectory }
            )
        );
        DirectoryLease? stage = null;
        try
        {
            // Re-read after retaining ancestors. No read lease survives Ready,
            // because the parent must replace the marker and remove the intent.
            ProfileOwnership.Verify(layout.Root);
            if (PortableLayout.Read(layout.Root) != layout || ReadPending(layout) != pending)
                throw new InvalidDataException("The pending portable data move changed.");
            if (!committed)
            {
                ProfileOwnership.Verify(pending.SourceDataDirectory);
                RequireIdentity(
                    ancestors.Identity(pending.SourceDataDirectory),
                    pending.SourceIdentity
                );
            }
            string copy =
                !committed && Directory.Exists(pending.StagingDirectory)
                    ? pending.StagingDirectory
                    : pending.DestinationDataDirectory;
            stage = new DirectoryLease([copy]);
            SnapshotSecurity.VerifyPrivate(copy, true, true);
            RequireIdentity(stage.Identity(copy), pending.StageIdentity);
            return new(ancestors, stage);
        }
        catch
        {
            stage?.Dispose();
            ancestors.Dispose();
            throw;
        }
    }

    internal static PortableDataMove ReadPending(PortableLayout layout)
    {
        using var input = FileLease.Open(
            Path.Combine(layout.Root, "portable-data-move.json"),
            16384
        );
        var pending = UpdateProtocol.Read<PortableDataMove>(input.Read(16384), 16384);
        if (
            pending.SchemaVersion != 1
            || !Guid.TryParseExact(pending.OperationId, "D", out var operation)
            || operation == Guid.Empty
            || pending.OperationId != operation.ToString("D")
            || !DateTimeOffset.TryParseExact(
                pending.CreatedAtUtc,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _
            )
            || !ValidIdentity(pending.SourceIdentity)
            || !ValidIdentity(pending.StageIdentity)
            || pending.DestinationIdentity is not null
                && !ValidIdentity(pending.DestinationIdentity)
        )
            throw new InvalidDataException("The pending portable data move is invalid.");
        foreach (
            string selected in new[]
            {
                pending.SourceDataDirectory,
                pending.DestinationDataDirectory,
            }
        )
            if (
                !Path.IsPathFullyQualified(selected)
                || !PortableLayout
                    .ResolveDataDirectory(layout.Root, selected)
                    .Equals(selected, StringComparison.OrdinalIgnoreCase)
            )
                throw new InvalidDataException(
                    "The pending portable data move has an invalid location."
                );
        if (
            PortableLayout.SameOrChild(
                pending.SourceDataDirectory,
                pending.DestinationDataDirectory
            )
            || PortableLayout.SameOrChild(
                pending.DestinationDataDirectory,
                pending.SourceDataDirectory
            )
            || !UpdatePaths.Equal(layout.Data, pending.SourceDataDirectory)
                && !UpdatePaths.Equal(layout.Data, pending.DestinationDataDirectory)
        )
            throw new InvalidDataException(
                "The pending portable data locations overlap or changed."
            );
        string expectedStage = Path.Combine(
            Path.GetDirectoryName(pending.DestinationDataDirectory)!,
            "."
                + Path.GetFileName(pending.DestinationDataDirectory)
                + ".ahk-manager-move-"
                + pending.OperationId
        );
        if (!UpdatePaths.Equal(pending.StagingDirectory, expectedStage))
            throw new InvalidDataException("The pending portable staging directory is invalid.");
        UpdatePaths.NoRedirects(pending.StagingDirectory);
        return pending;
    }

    static bool ValidIdentity(PortableDirectoryIdentity? identity) =>
        identity is not null
        && new[] { identity.Dev, identity.Ino, identity.BirthtimeNs }.All(value =>
            value is { Length: > 0 and <= 30 } && value.All(char.IsAsciiDigit)
        );

    internal static PortableDirectoryIdentity Identify(Native.FileStamp identity) =>
        new(
            identity.Volume.ToString(CultureInfo.InvariantCulture),
            (((ulong)identity.IndexHigh << 32) | identity.IndexLow).ToString(
                CultureInfo.InvariantCulture
            ),
            (((Int128)identity.Creation - 116444736000000000) * 100).ToString(
                CultureInfo.InvariantCulture
            )
        );

    static void RequireIdentity(Native.FileStamp actual, PortableDirectoryIdentity expected)
    {
        if (Identify(actual) != expected)
            throw new InvalidDataException("A prepared portable data directory changed identity.");
    }

    internal void ReleaseStage()
    {
        if (stage is null)
            throw new InvalidDataException("The portable data staging lease was already released.");
        stage.Dispose();
        stage = null;
    }

    public void Dispose()
    {
        stage?.Dispose();
        stage = null;
        ancestors.Dispose();
    }

    internal static async Task<int> Run()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("Portable data migration requires Windows.");
            var layout =
                PortableLayout.FromParent()
                ?? throw new InvalidDataException("The parent is not a portable Manager.");
            PortableInitialization.VerifySourceHelper(layout);
            using var retained = Acquire(layout);
            Console.WriteLine("{\"ready\":true}");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            string? command;
            while ((command = await ReadCommand().ConfigureAwait(false)) is not null)
            {
                var parsed = UpdateProtocol.Read<ReleaseStageRequest>(
                    Encoding.UTF8.GetBytes(command),
                    256
                );
                if (!parsed.ReleaseStage)
                    throw new InvalidDataException("The portable lease command is invalid.");
                retained.ReleaseStage();
                Console.WriteLine("{\"stageReleased\":true}");
                await Console.Out.FlushAsync().ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception error) when (UpdateProtocol.Known(error))
        {
            return 65;
        }
    }

    static async Task<string?> ReadCommand()
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (await Console.In.ReadAsync(character).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n')
                return text.ToString();
            if (text.Length >= 256)
                throw new InvalidDataException("The portable lease command exceeds its limit.");
            text.Append(character[0]);
        }
        if (text.Length != 0)
            throw new InvalidDataException("The portable lease command is incomplete.");
        return null;
    }

    sealed record ReleaseStageRequest(bool ReleaseStage);
}
