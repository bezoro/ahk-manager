using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal sealed class BytePipe : Stream
{
    readonly Channel<byte[]> chunks = Channel.CreateBounded<byte[]>(128);
    byte[]? current;
    int offset;

    public void End() => chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> target,
        CancellationToken ct = default
    )
    {
        if (current is null || offset == current.Length)
        {
            if (!await chunks.Reader.WaitToReadAsync(ct))
                return 0;
            current = await chunks.Reader.ReadAsync(ct);
            offset = 0;
        }
        int count = Math.Min(target.Length, current.Length - offset);
        current.AsMemory(offset, count).CopyTo(target);
        offset += count;
        return count;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default
    ) => chunks.Writer.WriteAsync(bytes.ToArray(), ct);

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] bytes, int offset, int count) =>
        ReadAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] bytes, int offset, int count) =>
        WriteAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class FakeParent : IParentGuard
{
    public ParentProof Proof { get; } =
        new(
            100,
            133700000000000001,
            @"C:\fixture\Bezoro.AhkManager.exe",
            200,
            133700000000000002,
            "S-1-5-21-fixture",
            1,
            AuthorityMode.Normal
        );
    public bool Live = true;

    public bool Validate() => Live;

    public void Dispose() { }
}

internal sealed class FakeSession : IBackendSession
{
    public int Calls,
        Disposals;
    public readonly TaskCompletionSource<JsonElement> Hold = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    public bool HoldCalls;
    public bool StallDispose;
    readonly TaskCompletionSource disposal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    public event EventHandler<ManagerSnapshot>? Changed;

    public void Publish() => Changed?.Invoke(this, ManagerSnapshot.Empty);

    public async Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement payload,
        CancellationToken ct
    )
    {
        Interlocked.Increment(ref Calls);
        if (HoldCalls && action != "shutdown")
            return await Hold.Task.WaitAsync(ct);
        if (action == "shutdown")
            await DisposeAsync();
        return JsonSerializer.SerializeToElement(
            new OperationResult(Outcome.Confirmed),
            Protocol.Json.OperationResult
        );
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref Disposals);
        return StallDispose ? new(disposal.Task) : ValueTask.CompletedTask;
    }
}

internal sealed class FakeStartup : IUserStartupAdapter
{
    public string? Entry;
    public int Writes;

    public string? ReadEntry() => Entry;

    public bool CompareExchangeEntry(string? expected, string? replacement)
    {
        if (Entry != expected)
            return false;
        Entry = replacement;
        Writes++;
        return true;
    }

    public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
        Task.FromResult(new StartupInspection(true, [], []));
}

internal sealed class FakeManager : IManagerService
{
    public ManagerSnapshot Snapshot { get; set; } = ManagerSnapshot.Empty;
    public int Performs,
        Disposals;
    public OperationPlan? LastPerformed;
    public event EventHandler<ManagerSnapshot>? Changed
    {
        add { }
        remove { }
    }

    static Task<OperationResult> Success() =>
        Task.FromResult(new OperationResult(Outcome.Confirmed));

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<CatalogResult> AddFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken ct = default
    ) => Task.FromResult(new CatalogResult(new(Outcome.Confirmed), [], 0, []));

    public Task<CatalogResult> AddFolderAsync(string path, CancellationToken ct = default) =>
        AddFilesAsync([path], ct);

    public Task<OperationResult> RemoveAsync(Guid id, CancellationToken ct = default) => Success();

    public Task<OperationResult> SaveMetadataAsync(
        Guid id,
        ScriptMetadata metadata,
        bool consent = false,
        CancellationToken cancellationToken = default
    ) => Success();

    public Task<OperationResult> SaveCollectionAsync(
        Guid? id,
        string name,
        CancellationToken ct = default
    ) => Success();

    public Task<OperationResult> RemoveCollectionAsync(Guid id, CancellationToken ct = default) =>
        Success();

    public Task<OperationPlan> PrepareAsync(
        SelectionContext selection,
        ScriptOperation operation,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            new OperationPlan(
                Guid.NewGuid(),
                selection.ScriptId,
                operation,
                selection.StateRevision,
                selection.Query.Version,
                null,
                true,
                true,
                ReasonCode.None,
                selection
            )
        );

    public Task<OperationResult> PerformAsync(
        OperationPlan plan,
        SelectionContext currentSelection,
        bool confirmed,
        CancellationToken ct = default
    )
    {
        Performs++;
        LastPerformed = plan;
        return Task.FromResult(
            new OperationResult(
                currentSelection == plan.Selection && confirmed
                    ? Outcome.Confirmed
                    : Outcome.Failed,
                currentSelection == plan.Selection ? ReasonCode.None : ReasonCode.StaleSelection
            )
        );
    }

    public Task<OperationResult> SaveSettingsAsync(
        ManagerSettings settings,
        bool consent = false,
        CancellationToken cancellationToken = default
    ) => Success();

    public Task<OperationResult> RunLoginAsync(CancellationToken ct = default) => Success();

    public Task<ImmutableArray<ActivitySnapshot>> GetActivityAsync(
        ActivityQuery query,
        CancellationToken ct = default
    ) => Task.FromResult(ImmutableArray<ActivitySnapshot>.Empty);

    public Task<ImportPreview> PreviewImportAsync(string source, CancellationToken ct = default) =>
        Task.FromResult(new ImportPreview(source, "fixture", 0, [], true));

    public Task<OperationResult> ImportAsync(
        ImportPreview preview,
        CancellationToken ct = default
    ) => Success();

    public Task<OperationResult> RestoreBackupAsync(CancellationToken ct = default) => Success();

    public Task<OperationResult> RecoverPendingAsync(CancellationToken ct = default) => Success();

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
