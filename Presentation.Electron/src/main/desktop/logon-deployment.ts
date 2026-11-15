import path from 'node:path';
import {
  classifyDeploymentFailure,
  deploymentPhases,
  deploymentReasons,
  resolveDeployment,
  samePath,
  type DeploymentLayout,
  type DeploymentPhase,
  type DeploymentReason,
} from '../storage/deployment.js';

const admissionBudgetMs = 45_000;
const retryIntervalMs = 1_000;
const maximumAttempts = admissionBudgetMs / retryIntervalMs;
const waitCell = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT));
const retryable = (reason: DeploymentReason) =>
  reason === 'ResourceBusy' || reason === 'MarkerMissing' || reason === 'SnapshotChanged';

export interface LogonDeploymentDiagnostic {
  phase: DeploymentPhase;
  reason: DeploymentReason;
  attempts: number;
  durationMs: number;
  exhausted: boolean;
}

export class LogonDeploymentError extends Error {
  constructor(readonly diagnostic: LogonDeploymentDiagnostic) {
    super('Manager could not resolve portable storage at sign-in.');
    this.name = 'LogonDeploymentError';
  }
}

export function validLogonDeploymentDiagnostic(value: Record<string, unknown>): boolean {
  return (
    deploymentPhases.some((phase) => phase === value.phase) &&
    deploymentReasons.some((reason) => reason === value.reason) &&
    typeof value.attempts === 'number' &&
    Number.isSafeInteger(value.attempts) &&
    value.attempts > 0 &&
    value.attempts <= maximumAttempts &&
    typeof value.durationMs === 'number' &&
    Number.isSafeInteger(value.durationMs) &&
    value.durationMs >= 0 &&
    typeof value.exhausted === 'boolean' &&
    (!value.exhausted || retryable(value.reason as DeploymentReason))
  );
}

interface RetryTiming {
  now(): number;
  wait(milliseconds: number): void;
}
const realTiming: RetryTiming = {
  now: () => performance.now(),
  wait: (milliseconds) => {
    Atomics.wait(waitCell, 0, 0, milliseconds);
  },
};

/** Keep this synchronous so Electron cannot initialize the wrong userData while waiting. */
export function resolveStartupDeployment(
  executable: string,
  localAppDataDirectory: string,
  argv: readonly string[],
  packaged: boolean,
  timing: RetryTiming = realTiming,
): DeploymentLayout {
  if (
    !packaged ||
    process.platform !== 'win32' ||
    argv.length !== 3 ||
    argv[1] !== '--login' ||
    argv[2] !== '--background' ||
    !argv[0] ||
    !path.isAbsolute(argv[0]) ||
    !samePath(argv[0], executable)
  )
    return resolveDeployment(executable, localAppDataDirectory);

  const started = timing.now();
  for (let attempts = 1; ; attempts++) {
    let phase: DeploymentPhase = 'ApplicationPath';
    try {
      return resolveDeployment(executable, localAppDataDirectory, (current) => {
        phase = current;
      });
    } catch (error) {
      const reason = classifyDeploymentFailure(phase, error);
      const eligible = retryable(reason);
      const elapsed = () => timing.now() - started;
      if (
        eligible &&
        attempts < maximumAttempts &&
        elapsed() + retryIntervalMs < admissionBudgetMs
      ) {
        timing.wait(retryIntervalMs);
        if (elapsed() < admissionBudgetMs) continue;
      }
      throw new LogonDeploymentError({
        phase,
        reason,
        attempts,
        durationMs: Math.floor(elapsed()),
        exhausted: eligible,
      });
    }
  }
}
