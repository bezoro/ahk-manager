import type {
  HostState,
  ManagerSnapshot,
  OperationResult,
  ScriptSnapshot,
} from '../../shared/contracts.js';
import { hostText as t } from '../../shared/host-pt-BR.js';

const nonblockingGlobal = new Set([
  'StartupInactive',
  'StartupEntryConflict',
  'ActivityUnavailable',
]);
export const blockingScript = new Set([
  'ObservationIncomplete',
  'PendingOperation',
  'AccessDenied',
  'MultipleInstances',
  'IdentityChanged',
  'WindowUnverified',
  'CapabilityUnverified',
  'EffectOwnerUnavailable',
  'LegacyGuard',
  'ReadOnlySettings',
  'StorageFailure',
  'MissingFile',
]);
export function trustworthy(snapshot: ManagerSnapshot): boolean {
  return (
    snapshot.effectsAvailable &&
    !snapshot.readOnly &&
    snapshot.observedAt !== null &&
    !snapshot.issues.some((issue) => !nonblockingGlobal.has(issue.code))
  );
}
export function rowProblem(script: ScriptSnapshot): string | null {
  return (
    script.issues.find((issue) => blockingScript.has(issue.code))?.code ??
    (script.status === 'Stopped' && script.process === null
      ? null
      : script.status === 'Running' && script.process?.windowVerified
        ? (script.process.controlUnavailableReason ??
          (script.process.identity.scriptPath.toLowerCase() === script.path.toLowerCase()
            ? null
            : 'IdentityChanged'))
        : script.status)
  );
}

export const cancelled = (): OperationResult => ({
  outcome: 'CancelledBeforeDispatch',
  code: 'Cancelled',
  detail: null,
  correlationId: null,
});
export const stale = (): OperationResult => ({
  outcome: 'CancelledBeforeDispatch',
  code: 'StaleSelection',
  detail: null,
  correlationId: null,
});

export function readySnapshot(state: HostState): ManagerSnapshot {
  if (state.connection !== 'ready' || !state.snapshot) throw new Error(t.backendUnavailable);
  return state.snapshot;
}
