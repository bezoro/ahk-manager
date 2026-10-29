import { orderlyShutdown, withinDeadline, type ShutdownBoundary } from './lifecycle.js';

/** A timed-out drain or placement save must never dispatch a late backend shutdown. */
export async function shutdownForUpdate(
  boundary: ShutdownBoundary,
  milliseconds: number,
  schedule?: (fn: () => void, milliseconds: number) => () => void,
): Promise<void> {
  let active = true;
  const check = (): void => {
    if (!active) throw new Error('Update preparation was cancelled.');
  };
  try {
    await withinDeadline(
      () =>
        orderlyShutdown({
          ...boundary,
          drain: async () => {
            await boundary.drain();
            check();
          },
          persistPlacement: async () => {
            check();
            await boundary.persistPlacement();
            check();
          },
          shutdownBackend: async () => {
            check();
            await boundary.shutdownBackend();
          },
        }),
      milliseconds,
      schedule,
    );
    if (boundary.backendPresent() && !boundary.backendExited())
      throw new Error('The backend did not confirm shutdown.');
  } catch (error) {
    if (error instanceof Error && error.message === 'DeadlineExceeded')
      throw new Error('The backend did not confirm shutdown in time. The update was cancelled.');
    throw error;
  } finally {
    active = false;
  }
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
