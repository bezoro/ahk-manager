import type { ScriptOperation } from './contracts.js';
const stopExitReasons: Record<string, string> = {
  BatchBusy: 'Another stop-and-exit operation is in progress.',
  ObservationUntrusted: 'Process observation or backend authority has not been verified.',
  ConfirmationTooLarge:
    'The scope is too large for a complete native confirmation. No stop request was sent.',
  ScopeOrAuthorityChanged: 'The catalog or authority changed after confirmation.',
  UnexpectedPlan: 'The backend returned authorization for a different operation.',
  DiscardUnconfirmed:
    'Authorization disposal was not confirmed. It was not used to send another stop request.',
  FinalStateNotStopped: 'The final observation did not confirm that every script had stopped.',
  NotDispatched: 'Stop request not sent.',
  DeadlineExceeded: 'The final observation exceeded its 30-second deadline.',
};
const stopExitReason = (value: string) => stopExitReasons[value] ?? value;
/** Native host copy. Keep consent wording and dynamic target details together. */
export const hostText = {
  title: 'AHK Manager',
  cancel: 'Cancel',
  confirm: 'Confirm',
  unknownError: 'Unknown error.',
  administrationTitle: 'Reopen as administrator?',
  administrationDetail:
    'Windows will ask for your approval. The manager will reopen with administrator access only after the authenticated handoff. Running scripts will be preserved. Windows startup will remain in standard mode.',
  administrationPending:
    'The authority handoff is pending. No operation will be sent again. Wait for confirmation or check the handoff status.',
  administrationCliInvalid: 'Use --request-administrator once, without --login or --background.',
  loginResultTitle: 'Check script startup',
  administrationSetupDeferred:
    'The manager will stay in standard mode for this session. You can complete setup with Reopen as administrator.',
  startupSetupIncomplete:
    'The startup preference was saved, but Windows has not confirmed that it was applied.',
  continuationTitle: 'Continue starting standard scripts?',
  continuationContinue: 'Continue with Windows approval',
  continuationLater: 'Not now',
  continuationMenu: 'Continue script startup…',
  continuationCheck: 'Check script startup…',
  continuationDetail: (names: readonly string[]) =>
    `An administrator process prevents complete verification. Windows may ask for approval to reopen the manager and check processes before continuing. Only the standard scripts below that have not received a start request will be considered. They will continue without administrator access. Windows startup will remain in standard mode.\n\n${names.slice(0, 24).join('\n')}${names.length > 24 ? '\nSee Activity for the complete scope.' : ''}`,
  adoptionInvalid:
    'The continuation request is invalid. Open the manager normally to check its status. No start request will be repeated.',
  loginFailure: (code: string) =>
    code === 'ObservationIncomplete'
      ? 'The process inventory is incomplete. Scripts were preserved to avoid duplicate instances.'
      : code === 'PendingOperation' || code === 'LaunchUncertain'
        ? 'An earlier launch has not been confirmed. Affected scripts were not started again to avoid duplicate instances.'
        : code === 'StartupInactive'
          ? 'Windows startup is disabled or its entry needs review.'
          : code === 'ExternalStartupConflict'
            ? 'Other automatic startup settings could not be verified. Review them to avoid duplicate instances.'
            : `Startup was not completed (${code}). Check Activity and the script notices.`,
  windowsPackagedRequired:
    'Run the packaged Windows distribution. Tests use isolated dependencies.',
  invalidPath: 'Invalid path.',
  wrongFileType: 'The selection does not match the expected file type.',
  fileLimit: 'The selection exceeds the file limit.',
  pickerTitles: {
    scripts: 'Add AutoHotkey scripts',
    locate: 'Locate script',
    folder: 'Add scripts from a folder',
    interpreter: 'Select AutoHotkey v2',
    editor: 'Select editor',
    import: 'Select legacy catalog',
    migration: 'Select WinUI profile',
  },
  deleteTitle: 'Delete file?',
  deleteDetail: (file: string, pid: number | null) =>
    `${file}\n\n${pid === null ? '' : `The verified instance (PID ${pid}) will be stopped first, without force termination.\n`}The file will be moved to the Recycle Bin and its entry removed from the list. Windows may request administrator approval. If an error occurs after stopping, the script will remain stopped. Cancel preserves the file and its entry.`,
  locateStartupTitle: 'Keep this script enabled at login?',
  locateStartupDetail: (file: string) =>
    `${file}\n\nThe new path will keep the preference to run this script at login. This confirmation does not enable manager startup or run the script now.`,
  recycleUnknown:
    'The deadline expired. Recycling may still be in progress, or the file may already be in the Recycle Bin. Deletion will not be retried automatically.',
  recycleFailed:
    'Recycling was not confirmed. The catalog entry was preserved. Check the file and the Recycle Bin.',
  recycleStopped: 'The script was stopped and will remain stopped. ',
  fileFilters: {
    scripts: 'AutoHotkey scripts',
    catalog: 'JSON catalog',
    applications: 'Applications',
  },
  welcomeTitle: 'Welcome to AHK Manager',
  welcomeQuestion: 'Create a new profile or migrate a WinUI profile?',
  welcomeDetail:
    'Existing profiles and scripts will be preserved. Migration shows a preview and requires confirmation before copying files.',
  newProfile: 'Create new profile',
  chooseWinUiProfile: 'Select WinUI profile',
  tray: {
    open: 'Open AHK Manager',
    hide: 'Hide window',
    show: 'Show window',
    running: (count: number) => `Running scripts (${count})`,
    noneRunning: 'No confirmed running scripts',
    preferences: 'Preferences',
    exit: 'Exit and keep scripts running',
    stopExit: 'Stop scripts and exit…',
    cancelStopExit: 'Cancel stopping and stay open',
    stopping: (done: number, total: number) => `Stopping scripts (${done}/${total})…`,
  },
  stopExitBusy: 'Stop and exit is in progress. Wait or cancel from the tray icon.',
  stopExitTitle: 'Stop the catalog scripts and exit?',
  stopExitConsent: (
    items: readonly { name: string; path: string; pid: number | null; reason: string }[],
  ) =>
    `Scope: all ${items.length} scripts in this catalog, regardless of filters. Only the verified identities below can receive a stop request. Unverifiable, elevated, changed, or pending instances will be preserved. In those cases, the manager will stay open. No process will be forcefully terminated.\n\n${items.map((item) => `${item.name}\n${item.path}\nPID: ${item.pid ?? '—'} · ${item.reason === 'None' ? 'already stopped' : item.reason === 'NotDispatched' ? 'verified identity' : item.reason}`).join('\n\n')}\n\nCancel preserves all scripts. Cancelling while stopping prevents further requests. Scripts already stopped will not be restarted.`,
  stopExitPartialTitle: 'The manager will stay open',
  stopExitPartial: (receipt: {
    confirmedStops: number;
    cancelled: boolean;
    expired: boolean;
    items: readonly { name: string; reason: string; outcome: string }[];
    problems: readonly string[];
  }) =>
    `${receipt.confirmedStops} stops confirmed.${receipt.cancelled ? ' Operation cancelled.' : ''}${receipt.expired ? ' Dispatch deadline expired.' : ''}\nScripts already stopped will remain stopped. No process was forcefully terminated. The scripts below did not have a confirmed stop. You can choose "Exit and keep scripts running" separately.\n\n${receipt.items
      .filter((item) => item.outcome === 'untouched' || item.outcome === 'unknown')
      .slice(0, 24)
      .map((item) => `${item.name.slice(0, 120)}: ${stopExitReason(item.reason).slice(0, 240)}`)
      .join('\n')}\n${receipt.problems
      .slice(0, 8)
      .map((value) => stopExitReason(value).slice(0, 400))
      .join('\n')}`,
  closing: 'The manager is closing.',
  documentChanged: 'The document changed during the operation. Check Activity for the result.',
  shutdownUnconfirmed: 'Shutdown not confirmed',
  backendStillAlive: (pid: number | null) =>
    `The previous backend (PID ${pid}) has not confirmed exit. Restart is blocked.`,
  shutdownDeadline: (pid: number | null) =>
    `The 15-second deadline expired. The exit state of backend PID ${pid ?? '?'} is unknown. Check operations.json in the profile. No script or process was forcefully terminated.`,
  backendClosed: (code: string | number | null) =>
    `Backend closed (${code ?? 'unknown'}). Running scripts were preserved.`,
  backendChannelClosed:
    'The connection to the process manager closed. Running scripts remain independent.',
  dispatchedUnknown: (action: string, id: string) =>
    `The result of request ${action} (${id}) could not be confirmed. It will not be sent again automatically.`,
  backendShutdownUnknown: (pid: number | null) =>
    `Unknown shutdown state for backend PID ${pid}. No process was forcefully terminated.`,
  backendUnavailable: 'Backend unavailable. No operation was sent again.',
  queueFull: 'The operation queue is full.',
  documentClosed: 'The document closed before dispatch.',
  removeTitle: 'Remove from the list?',
  removeDetail: (name: string, file: string) =>
    `${name}\n${file}\n\nThe file and running process will be preserved.`,
  scriptStartupTitle: 'Run this script after login?',
  scriptStartupDetail: (name: string, file: string) =>
    `${name}\n${file}\n\nAHK Manager will apply the preference when you sign in to Windows.`,
  removeCollectionTitle: 'Remove collection?',
  removeCollectionDetail: (name: string) => `${name}\n\nScripts and files will be preserved.`,
  operationTitle: 'Confirm operation',
  operationDetail: (action: string, name: string, file: string, pid: number | null) =>
    `${action}\n${name}\n${file}${pid === null ? '' : `\nPID: ${pid}`}`,
  operations: {
    Start: 'Start script',
    Stop: 'Request script shutdown',
    Restart: 'Restart script',
    TogglePause: 'Toggle pause',
    ToggleSuspend: 'Toggle hotkey suspension',
    Edit: 'Open script in editor',
    OpenFolder: 'Open script folder',
    Recover: 'Recover observation',
    RecycleFile: 'Delete file',
  } satisfies Record<ScriptOperation, string>,
  selectExecutable: 'Select the executable with the Windows file picker.',
  managerStartupTitle: 'Start AHK Manager with Windows?',
  managerStartupDetail:
    'The manager will start when this user signs in. Administrator mode uses a scheduled task and may request Windows approval. Standard mode uses a per-user startup entry.',
  previewsFull: 'Too many previews are pending.',
  importTitle: 'Import catalog?',
  importDetail: (source: string, count: number) =>
    `${source}\n${count} scripts\n\nThe source file will be preserved.`,
  migrationTitle: 'Migrate WinUI profile?',
  migrationDetail: (source: string, count: number, pending: number) =>
    `${source}\n${count} files; ${pending} pending operations.\n\nAn exact backup will be saved in the new profile. The source and scripts will be preserved. No startup entry will be transferred at this stage.`,
  transferTitle: 'Transfer WinUI startup?',
  transferDetail: (oldPath: string | null, newPath: string) =>
    `Source: ${oldPath}\nDestination: ${newPath}\n\nOnly the verified existing entry will be transferred. No duplicate entry will be created.`,
  restoreTitle: 'Restore backup?',
  restoreDetail:
    'The backend will verify the backup and revision before replacing settings. Scripts on disk will be preserved.',
} as const;
