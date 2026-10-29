using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService : IManagerService
{
    readonly SemaphoreSlim lane = new(1, 1);
    readonly SettingsStore store;
    readonly OperationJournal journal;
    readonly ActivityStore activity;
    readonly LegacyImportService importer;
    readonly IEffectOwner owner;
    readonly IProcessManager processes;
    readonly UserStartupService startup;
    readonly HotkeyAnalyzer analyzer = new();
    readonly ExternalEditorService editor = new();
    readonly Dictionary<Guid, HotkeyAnalysis> analyses = [];
    readonly Dictionary<Guid, (OperationPlan Plan, DateTimeOffset Time)> plans = [];
    readonly Dictionary<Guid, (RecyclePlan Plan, DateTimeOffset Time)> recyclePlans = [];
    SettingsDocument document = new();
    ObservedProcesses observed = new([], false, []);
    ImmutableArray<Issue> storageIssues = [];
    ManagerSnapshot snapshot = ManagerSnapshot.Empty;
    DateTimeOffset? observedAt;
    bool loaded,
        disposed;
    int refreshQueued;
    public ManagerSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event EventHandler<ManagerSnapshot>? Changed;
    public string DataRoot { get; }

    public static ManagerService CreateDefault(string? dataRoot = null)
    {
        var owner = new UserEffectOwner();
        return new(
            dataRoot
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bezoro",
                    "AhkManagerWinUI"
                ),
            owner,
            new ProcessManager(owner, documentedControlsProven: true),
            new UserStartupService(
                new RegistryStartupAdapter(),
                Environment.ProcessPath
                    ?? throw new InvalidOperationException("Manager executable unavailable.")
            ),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bezoro",
                "AhkManager"
            )
        );
    }

    // Trusted host composition only; no renderer or script input reaches this seam.
    public static ManagerService CreateForTrustedStartupHost(
        string dataRoot,
        string startupHostExecutable,
        out IEffectOwner effectOwner
    )
    {
        string executable = ScriptPaths.Canonical(startupHostExecutable, false);
        if (
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(executable)
                .Equals("AhkManager.Backend.exe", StringComparison.OrdinalIgnoreCase)
        )
            throw new ArgumentException("A validated manager host executable is required.");
        var owner = new UserEffectOwner();
        effectOwner = owner;
        return new(
            dataRoot,
            owner,
            new ProcessManager(owner, documentedControlsProven: true),
            new UserStartupService(new RegistryStartupAdapter(), executable),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bezoro",
                "AhkManager"
            )
        );
    }

    public ManagerService(
        string dataRoot,
        IEffectOwner owner,
        IProcessManager processes,
        UserStartupService startup,
        string? legacyAuthorityDirectory = null
    )
    {
        DataRoot = ScriptPaths.LocalPath(dataRoot);
        this.owner = owner;
        this.processes = processes;
        this.startup = startup;
        store = new(Path.Combine(DataRoot, "settings.json"));
        journal = new(Path.Combine(DataRoot, "operations.json"));
        activity = new(DataRoot);
        importer = new(DataRoot, owner, legacyAuthorityDirectory);
        processes.Invalidated += ProcessInvalidated;
    }

    async Task<T> Exclusive<T>(Func<Task<T>> action, CancellationToken ct)
    {
        return await Task.Run(
                async () =>
                {
                    await lane.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);
                        return await action().ConfigureAwait(false);
                    }
                    finally
                    {
                        lane.Release();
                    }
                },
                ct
            )
            .ConfigureAwait(false);
    }

    async Task<OperationResult> ResultAction(
        Func<Task<OperationResult>> action,
        CancellationToken ct
    )
    {
        try
        {
            return await Exclusive(action, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        }
        catch (Exception ex)
            when (SafeJsonFiles.Known(ex)
                || ex is System.ComponentModel.Win32Exception or InvalidOperationException
            )
        {
            return new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message);
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await Exclusive(
                async () =>
                {
                    if (loaded)
                        return true;
                    var result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                    document = result.Document;
                    storageIssues = result.Issues;
                    try
                    {
                        await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                        when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException)
                    {
                        storageIssues = storageIssues.Add(
                            new(ReasonCode.PendingOperation, ex.Message)
                        );
                    }
                    await LoadStartupRecovery().ConfigureAwait(false);
                    if (document.Settings.InterpreterPath is null)
                    {
                        var candidates = await new AutoHotkeyDiscovery()
                            .DiscoverAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (candidates.Length > 0)
                            document = document with
                            {
                                Settings = document.Settings with
                                {
                                    InterpreterPath = candidates[0].Path,
                                },
                            };
                    }
                    loaded = true;
                    await ObserveCore(cancellationToken).ConfigureAwait(false);
                    await ReconcileObservedCore(cancellationToken).ConfigureAwait(false);
                    await Publish(true, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!loaded)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await Exclusive(
                async () =>
                {
                    await ObserveCore(cancellationToken).ConfigureAwait(false);
                    await ReconcileObservedCore(cancellationToken).ConfigureAwait(false);
                    await Publish(true, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    async Task<OperationResult> Commit(SettingsDocument next, Guid? scriptId, CancellationToken ct)
    {
        var result = await store.SaveAsync(next, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            document = next;
            await Log(scriptId, null, result, CancellationToken.None).ConfigureAwait(false);
            await Publish(true, CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }

    async Task Log(
        Guid? script,
        ScriptOperation? operation,
        OperationResult result,
        CancellationToken ct
    )
    {
        var logged = await activity
            .AppendAsync(
                new(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    script,
                    operation,
                    result.Outcome,
                    result.Code,
                    result.Detail
                ),
                ct
            )
            .ConfigureAwait(false);
        if (!logged.Succeeded && !storageIssues.Any(i => i.Code == ReasonCode.ActivityUnavailable))
            storageIssues = storageIssues.Add(new(ReasonCode.ActivityUnavailable, logged.Detail));
    }

    public Task<ImmutableArray<ActivitySnapshot>> GetActivityAsync(
        ActivityQuery query,
        CancellationToken cancellationToken = default
    ) => Exclusive(() => activity.ReadAsync(query, cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await lane.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            processes.Invalidated -= ProcessInvalidated;
            await processes.DisposeAsync().ConfigureAwait(false);
            owner.Dispose();
        }
        finally
        {
            lane.Release();
        }
    }
}
