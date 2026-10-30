import { shutdownWithinDeadline, type ShutdownBoundary } from '../lifecycle.js';

/** A timed-out drain or placement save must never dispatch a late backend shutdown. */
export async function shutdownForUpdate(
  boundary: ShutdownBoundary,
  milliseconds: number,
  schedule?: (fn: () => void, milliseconds: number) => () => void,
): Promise<void> {
  await shutdownWithinDeadline(boundary, milliseconds, {
    schedule,
    cancelled: 'Update preparation was cancelled.',
    timedOut: 'The backend did not confirm shutdown in time. The update was cancelled.',
    unconfirmed: 'The backend did not confirm shutdown.',
  });
}

/** The commit callback is unreachable until the bounded shutdown is confirmed. */
export async function shutdownAndCommitUpdate(
  boundary: ShutdownBoundary,
  milliseconds: number,
  commit: () => Promise<void>,
  schedule?: (fn: () => void, milliseconds: number) => () => void,
): Promise<void> {
  await shutdownForUpdate(boundary, milliseconds, schedule);
  await commit();
}
