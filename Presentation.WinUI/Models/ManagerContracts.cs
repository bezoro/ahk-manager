using System.Collections.Immutable;

namespace Bezoro.AhkManager.WinUI.Models;

public enum ScriptStatus { Stopped, Running, Unknown, Multiple, Missing, Blocked }
public enum ExecutionOrigin { None, Manager, External, Recovered }
public enum Outcome { Accepted, Pending, Confirmed, Partial, CancelledBeforeDispatch, Failed, Unknown }
public enum ReasonCode
{
    None, InvalidPath, MissingFile, UnsupportedInterpreter, AccessDenied, ObservationIncomplete,
    AlreadyRunning, MultipleInstances, IdentityChanged, WindowUnverified, CapabilityUnverified,
    EffectOwnerUnavailable, LegacyGuard, PendingOperation, StaleSelection, ReadOnlySettings,
    InvalidSettings, UnsupportedSchema, RecoveredBackup, ConcurrentSettingsChange, StorageFailure,
    Cancelled, DispatchFailed, ExitNotObserved, StateNotObservable, RestartPartial, LaunchUncertain,
    StartupConsentRequired, StartupEntryConflict, StartupInactive, StartupAlreadyProcessed,
    ImportSourceChanged, ImportInvalid, ImportAvailable, FolderLimitReached, PartialAnalysis,
    IncludeUnresolved, IncludeLimitReached, DynamicHotkeys, PossibleConflict, EditorUnavailable,
    ActivityUnavailable, InvalidMetadata, ManualElevationRequired, ExternalStartupConflict
}
public enum ScriptOperation { Start, Stop, Restart, TogglePause, ToggleSuspend, Edit, OpenFolder, Recover, RecycleFile }
public enum DeclaredGesture { DoubleClick }
public enum HotkeyOrigin { Parsed, Manual, Unanalysed }
public enum AnalysisCoverage { Partial, Unanalysable }
public enum StatusFilter { All, Running, Stopped, Problems }
public enum ScriptSort { Name, Status, Collection }
public enum AppThemePreference { System, Light, Dark }
public enum CloseBehavior { HideToTray, Exit }

public sealed record Issue(ReasonCode Code, string? Detail = null, Guid? ScriptId = null);
public sealed record ProcessIdentity(int Pid, long CreationFileTime, string ExecutablePath,
    string ScriptPath, string UserSid, int SessionId, string ScriptHash = "", string InterpreterHash = "");
public sealed record ProcessSnapshot(ProcessIdentity Identity, ExecutionOrigin Origin,
    string InterpreterVersion, DateTimeOffset StartedAt, bool? Paused = null, bool? Suspended = null,
    bool WindowVerified = false, ReasonCode? ControlUnavailableReason = null)
{
    public ReasonCode? ControlUnavailableReason
    {
        get;
        init => field = Normalize(value);
    } = Normalize(ControlUnavailableReason);
    static ReasonCode? Normalize(ReasonCode? reason) => reason switch
    {
        null or ReasonCode.None => null,
        ReasonCode.AccessDenied or ReasonCode.ManualElevationRequired => reason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
public sealed record HotkeySnapshot(string Chord, string NormalizedChord, HotkeyOrigin Origin,
    string? Context = null, string? SourcePath = null, int? Line = null, bool IsRemap = false,
    bool PossibleConflict = false, DeclaredGesture? DeclaredGesture = null);
public sealed record ScriptFileIdentity(string Path, string FileId, string Hash);
public sealed record RecyclePlan(Guid OperationId, Guid ScriptId, string Path, ScriptFileIdentity? File,
    ProcessIdentity? ExpectedIdentity, SelectionContext Selection, bool Allowed, ReasonCode Reason);
public sealed record RecycleAuthorization(OperationResult Result, ScriptFileIdentity? File = null, bool Stopped = false);
public sealed record ScriptSnapshot(Guid Id, string Path, string FriendlyName, string Description,
    Guid? CollectionId, bool? RunAtLogin, bool RunAsAdministrator, bool LaunchBlocked,
    ScriptStatus Status, ProcessSnapshot? Process, ImmutableArray<HotkeySnapshot> Hotkeys,
    AnalysisCoverage Coverage, ImmutableArray<Issue> Issues, string? HeaderDescription = null);
public sealed record CollectionSnapshot(Guid Id, string Name);
public sealed record WindowPlacement(int X = 100, int Y = 100, int Width = 1200, int Height = 780,
    bool Maximized = false, double Dpi = 96);
public sealed record ManagerSettings
{
    public string? InterpreterPath { get; init; }
    public string? EditorPath { get; init; }
    public ImmutableArray<string> EditorArguments { get; init; } = [];
    public bool LaunchManagerAtLogin { get; init; } = true;
    public bool LaunchManagerAsAdministrator { get; init; } = true;
    public bool ShowWindowAtLogin { get; init; }
    public bool StartInTray { get; init; }
    public AppThemePreference Theme { get; init; } = AppThemePreference.System;
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.HideToTray;
    public WindowPlacement Window { get; init; } = new();
}
public sealed record ActivitySnapshot(Guid Id, DateTimeOffset Timestamp, Guid? ScriptId,
    ScriptOperation? Operation, Outcome Outcome, ReasonCode Code, string? Detail = null);
public sealed record ManagerSnapshot(long Revision, ImmutableArray<ScriptSnapshot> Scripts,
    ImmutableArray<CollectionSnapshot> Collections, ManagerSettings Settings,
    ImmutableArray<ActivitySnapshot> Activity, ImmutableArray<Issue> Issues,
    bool ReadOnly, bool EffectsAvailable, DateTimeOffset? ObservedAt,
    ReasonCode? NormalLaunchUnavailableReason = null)
{
    public ReasonCode? NormalLaunchUnavailableReason
    {
        get;
        init => field = NormalizeLaunchReason(value);
    } = NormalizeLaunchReason(NormalLaunchUnavailableReason);
    static ReasonCode? NormalizeLaunchReason(ReasonCode? reason) => reason switch
    {
        null or ReasonCode.None => null,
        ReasonCode.AccessDenied or ReasonCode.CapabilityUnverified => reason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
    public static ManagerSnapshot Empty { get; } = new(0, [], [], new(), [], [], false, false, null);
    public int RunningCount => Scripts.Count(s => s.Status == ScriptStatus.Running);
    public int ProblemCount => Issues.Length + Scripts.Sum(s => s.Issues.Length);
}
public sealed record ScriptQuery(string Search = "", StatusFilter Status = StatusFilter.All,
    ScriptSort Sort = ScriptSort.Name, Guid? CollectionId = null, long Version = 0);
public sealed record ViewProjection(ImmutableArray<Guid> VisibleIds, Guid? EffectiveSelectedId,
    long StateRevision, long QueryVersion);
public sealed record SelectionContext(Guid ScriptId, ScriptQuery Query, long StateRevision);
public sealed record OperationPlan(Guid CorrelationId, Guid ScriptId, ScriptOperation Operation,
    long StateRevision, long QueryVersion, ProcessIdentity? ExpectedIdentity, bool Allowed,
    bool RequiresConfirmation, ReasonCode Reason, SelectionContext Selection);
public sealed record OperationResult(Outcome Outcome, ReasonCode Code = ReasonCode.None,
    string? Detail = null, Guid? CorrelationId = null)
{
    public bool Succeeded => Outcome == Outcome.Confirmed;
}
public sealed record CatalogResult(OperationResult Result, ImmutableArray<Guid> Added,
    int DuplicateCount, ImmutableArray<Issue> Issues);
public sealed record ScriptMetadata(string FriendlyName, string Description, Guid? CollectionId,
    bool? RunAtLogin, bool RunAsAdministrator, bool LaunchBlocked,
    ImmutableArray<string> ManualHotkeys);
public sealed record ActivityQuery(Guid? ScriptId = null, Outcome? Outcome = null, string Search = "");
public sealed record ImportPreview(string SourcePath, string SourceHash, int ScriptCount,
    ImmutableArray<Issue> Issues, bool CanImport);

// Events may originate on a worker thread. The UI owns DispatcherQueue delivery.
// Closing/disposal releases observations and handles; it never stops scripts.
public interface IManagerService : IAsyncDisposable
{
    ManagerSnapshot Snapshot { get; }
    event EventHandler<ManagerSnapshot>? Changed;
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<CatalogResult> AddFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<CatalogResult> AddFolderAsync(string path, CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveAsync(Guid scriptId, CancellationToken cancellationToken = default);
    Task<OperationResult> RelinkAsync(SelectionContext selection, string oldPath, string replacementPath, bool startupConsent, CancellationToken cancellationToken = default) => Task.FromResult(new OperationResult(Outcome.Failed, ReasonCode.CapabilityUnverified));
    Task<RecyclePlan> PrepareRecycleAsync(SelectionContext selection, CancellationToken cancellationToken = default) => Task.FromResult(new RecyclePlan(Guid.NewGuid(), selection.ScriptId, "", null, null, selection, false, ReasonCode.CapabilityUnverified));
    Task<RecycleAuthorization> AuthorizeRecycleAsync(Guid operationId, SelectionContext selection, bool confirmed, CancellationToken cancellationToken = default) => Task.FromResult(new RecycleAuthorization(new(Outcome.Failed, ReasonCode.CapabilityUnverified)));
    Task<OperationResult> CompleteRecycleAsync(Guid operationId, Outcome receipt, string? detail, CancellationToken cancellationToken = default) => Task.FromResult(new OperationResult(Outcome.Failed, ReasonCode.CapabilityUnverified));
    Task<OperationResult> SaveMetadataAsync(Guid scriptId, ScriptMetadata metadata, bool startupConsent = false, CancellationToken cancellationToken = default);
    Task<OperationResult> SaveCollectionAsync(Guid? collectionId, string name, CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task<OperationPlan> PrepareAsync(SelectionContext selection, ScriptOperation operation, CancellationToken cancellationToken = default);
    Task<OperationResult> PerformAsync(OperationPlan plan, SelectionContext currentSelection, bool confirmed, CancellationToken cancellationToken = default);
    Task<OperationResult> SaveSettingsAsync(ManagerSettings settings, bool startupConsent = false, CancellationToken cancellationToken = default);
    Task<OperationResult> RunLoginAsync(CancellationToken cancellationToken = default);
    Task<ImmutableArray<ActivitySnapshot>> GetActivityAsync(ActivityQuery query, CancellationToken cancellationToken = default);
    Task<ImportPreview> PreviewImportAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<OperationResult> ImportAsync(ImportPreview preview, CancellationToken cancellationToken = default);
    Task<OperationResult> RestoreBackupAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RecoverPendingAsync(CancellationToken cancellationToken = default);
}
