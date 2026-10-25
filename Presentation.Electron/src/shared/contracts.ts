/** Lossless decimal strings are used for every .NET Int64 field. */
export type Decimal = string;
export type AuthorityMode = 'Normal' | 'Administrator';
export type ControlUnavailableReason = 'AccessDenied' | 'ManualElevationRequired';
export type NormalLaunchUnavailableReason = 'AccessDenied' | 'CapabilityUnverified';
export type ElevationState = 'PendingApproval' | 'Ready' | 'CommitPending' | 'CommitAcked' | 'Cancelled' | 'Denied' | 'Failed' | 'Expired' | 'Completed' | 'Unknown';
export interface ElevationStatus { operationId: string; revision: Decimal; state: ElevationState; reason: string | null; receiptId: string | null; canResumeNormal: boolean }
export type ScriptStatus = 'Stopped' | 'Running' | 'Unknown' | 'Multiple' | 'Missing' | 'Blocked';
export type ScriptOperation = 'Start' | 'Stop' | 'Restart' | 'TogglePause' | 'ToggleSuspend' | 'Edit' | 'OpenFolder' | 'Recover';
export type Outcome = 'Accepted' | 'Pending' | 'Confirmed' | 'Partial' | 'CancelledBeforeDispatch' | 'Failed' | 'Unknown';
export type AppThemePreference = 'System' | 'Light' | 'Dark';
export interface Issue { code: string; detail: string | null; scriptId: string | null }
export interface ProcessIdentity { pid: number; creationFileTime: Decimal; executablePath: string; scriptPath: string; userSid: string; sessionId: number; scriptHash: string; interpreterHash: string }
export interface ProcessSnapshot { identity: ProcessIdentity; origin: 'None' | 'Manager' | 'External' | 'Recovered'; interpreterVersion: string; startedAt: string; paused: boolean | null; suspended: boolean | null; windowVerified: boolean; controlUnavailableReason?: ControlUnavailableReason | null }
export interface HotkeySnapshot { chord: string; normalizedChord: string; origin: 'Parsed' | 'Manual' | 'Unanalysed'; context: string | null; sourcePath: string | null; line: number | null; isRemap: boolean; possibleConflict: boolean }
export interface ScriptSnapshot { id: string; path: string; friendlyName: string; description: string; headerDescription?: string | null; collectionId: string | null; runAtLogin: boolean | null; runAsAdministrator: boolean; launchBlocked: boolean; status: ScriptStatus; process: ProcessSnapshot | null; hotkeys: HotkeySnapshot[]; coverage: 'Partial' | 'Unanalysable'; issues: Issue[] }
export interface CollectionSnapshot { id: string; name: string }
export interface WindowPlacement { x: number; y: number; width: number; height: number; maximized: boolean; dpi: number }
export interface ManagerSettings { interpreterPath: string | null; editorPath: string | null; editorArguments: string[]; launchManagerAtLogin: boolean; showWindowAtLogin: boolean; theme: AppThemePreference; closeBehavior: 'HideToTray' | 'Exit'; window: WindowPlacement }
export interface ActivitySnapshot { id: string; timestamp: string; scriptId: string | null; operation: ScriptOperation | null; outcome: Outcome; code: string; detail: string | null }
export interface ManagerSnapshot { revision: Decimal; scripts: ScriptSnapshot[]; collections: CollectionSnapshot[]; settings: ManagerSettings; activity: ActivitySnapshot[]; issues: Issue[]; readOnly: boolean; effectsAvailable: boolean; observedAt: string | null; runningCount?: number; problemCount?: number; normalLaunchUnavailableReason?: NormalLaunchUnavailableReason | null }
export interface ScriptQuery { search: string; status: 'All' | 'Running' | 'Stopped' | 'Problems'; sort: 'Name' | 'Status' | 'Collection'; collectionId: string | null; version: Decimal }
export interface SelectionContext { scriptId: string; query: ScriptQuery; stateRevision: Decimal }
export interface OperationPlan { correlationId: string; scriptId: string; operation: ScriptOperation; stateRevision: Decimal; queryVersion: Decimal; expectedIdentity: ProcessIdentity | null; allowed: boolean; requiresConfirmation: boolean; reason: string; selection: SelectionContext }
export interface OperationResult { outcome: Outcome; code: string; detail: string | null; correlationId: string | null; succeeded?: boolean }
export interface CatalogResult { result: OperationResult; added: string[]; duplicateCount: number; issues: Issue[] }
export interface ScriptMetadata { friendlyName: string; description: string; collectionId: string | null; runAtLogin: boolean | null; runAsAdministrator: boolean; launchBlocked: boolean; manualHotkeys: string[] }
export interface ActivityQuery { scriptId?: string | null; outcome?: Outcome | null; search?: string }
export interface ImportPreview { sourcePath: string; sourceHash: string; scriptCount: number; issues: Issue[]; canImport: boolean }
export interface MigrationPreview { sourcePath: string; fingerprint: string; canMigrate: boolean; issues: Issue[]; [key: string]: unknown }
export interface AdministrationUiGuard { dirtyDrafts: boolean; dialogOpen: boolean; pendingWork: boolean }
export interface HostState { snapshot: ManagerSnapshot | null; connection: 'starting' | 'ready' | 'failed' | 'closing'; error: string | null; busy: boolean; authorityMode: AuthorityMode | null; administrationPending: boolean }
export type AppState = HostState;
export interface ThemeState { dark: boolean; highContrast: boolean; reducedMotion: boolean; accent: string; material: 'mica' | 'solid' }
export interface AhkManagerApi {
  getState(): Promise<HostState>;
  onState(listener: (state: HostState) => void): () => void;
  refresh(): Promise<void>;
  addFiles(): Promise<CatalogResult | null>;
  addFolder(): Promise<CatalogResult | null>;
  removeScript(scriptId: string, revision: Decimal): Promise<OperationResult>;
  saveMetadata(scriptId: string, metadata: ScriptMetadata, revision: Decimal): Promise<OperationResult>;
  saveCollection(collectionId: string | null, name: string): Promise<OperationResult>;
  removeCollection(collectionId: string, revision: Decimal): Promise<OperationResult>;
  operate(operation: ScriptOperation, selection: SelectionContext): Promise<OperationResult>;
  updateSelection(selection: SelectionContext | null): void;
  saveSettings(settings: ManagerSettings): Promise<OperationResult>;
  pickInterpreter(): Promise<string | null>;
  pickEditor(): Promise<string | null>;
  getActivity(query: ActivityQuery): Promise<ActivitySnapshot[]>;
  previewImport(): Promise<{ token: string; preview: ImportPreview } | null>;
  commitImport(token: string): Promise<OperationResult>;
  migrateProfile(): Promise<OperationResult | null>;
  transferStartup(): Promise<OperationResult>;
  restoreBackup(): Promise<OperationResult>;
  recoverPending(): Promise<OperationResult>;
  retryBackend(): Promise<void>;
  requestAdministration(): Promise<OperationResult>;
  onAdministrationPreflight(provider: () => AdministrationUiGuard): () => void;
  copyText(text: string): Promise<void>;
  getTheme(): Promise<ThemeState>;
  onTheme(listener: (theme: ThemeState) => void): () => void;
  onOpenPreferences(listener: () => void): () => void;
}
declare global { interface Window { ahkManager: AhkManagerApi } }
