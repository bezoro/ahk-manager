import { orderlyShutdown, withinDeadline, type ShutdownBoundary } from '../lifecycle.js';

/** A timed-out drain or placement save must never dispatch a late backend shutdown. */
export async function shutdownForUpdate(
  boundary: ShutdownBoundary,
  milliseconds: number,
  schedule?: (fn: () => void, milliseconds: number) => () => void,
): Promise<void> {
  let active = true;
  const check = (): void => {
    if (!active) throw new Error('A preparação da atualização foi cancelada.');
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
      throw new Error('O backend não confirmou o encerramento.');
  } catch (error) {
    if (error instanceof Error && error.message === 'DeadlineExceeded')
      throw new Error(
        'O backend não confirmou o encerramento no prazo. A atualização foi cancelada.',
      );
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
