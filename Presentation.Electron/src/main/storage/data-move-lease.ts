import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { getStorageState } from './data-migration.js';
import type { DeploymentLayout } from './deployment.js';

export interface DataMoveBoundary {
  check(): void;
  beforePromotion(): Promise<void>;
}

const noLease: DataMoveBoundary = { check: () => {}, beforePromotion: async () => {} };
type StartLease = (helper: string) => ChildProcess;
const startLease: StartLease = (helper) =>
  spawn(helper, ['--lease-portable-data-move'], {
    shell: false,
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'ignore'],
  });

/** A native child pins the source, private staging folder and their namespace while copying. */
export async function withDataMoveLease<T>(
  layout: DeploymentLayout,
  action: (boundary: DataMoveBoundary) => Promise<T>,
  start: StartLease = startLease,
): Promise<T> {
  if (layout.mode !== 'portable' || !getStorageState(layout).pendingDataDirectory)
    return action(noLease);
  const child = start(
    path.join(layout.applicationDirectory, 'backend', 'update', 'AhkManager.UpdateHost.exe'),
  );
  if (!child.stdin || !child.stdout) throw new Error('The data move lease could not be opened.');
  let failure: Error | null = null;
  let closed = false;
  let finishing = false;
  let buffer = '';
  let receivedBytes = 0;
  let expected: 'ready' | 'stageReleased' | null = 'ready';
  let resolveMessage: (() => void) | null = null;
  let rejectMessage: ((error: Error) => void) | null = null;
  let messageTimer: ReturnType<typeof setTimeout> | undefined;
  const fail = (error: Error): void => {
    failure ??= error;
    rejectMessage?.(failure);
    resolveMessage = null;
    rejectMessage = null;
    clearTimeout(messageTimer);
  };
  const check = (): void => {
    if (failure) throw failure;
    if (closed || child.exitCode !== null)
      throw new Error('The data move lease ended. The original data was preserved.');
  };
  const waitFor = (field: 'ready' | 'stageReleased'): Promise<void> => {
    expected = field;
    return new Promise<void>((resolve, reject) => {
      resolveMessage = resolve;
      rejectMessage = reject;
      messageTimer = setTimeout(() => {
        fail(new Error('The data move lease did not respond. The original data was preserved.'));
      }, 15000);
    });
  };
  const ready = waitFor('ready');
  const exit = new Promise<void>((resolve, reject) => {
    child.once('error', (error) => {
      fail(error);
      closed = true;
      reject(error);
    });
    child.once('exit', (code) => {
      closed = true;
      if (!finishing || code !== 0) {
        const error = new Error(
          'The data move lease ended unexpectedly. The original data was preserved.',
        );
        fail(error);
        reject(error);
      } else resolve();
    });
  });
  // An early child failure is consumed here and rethrown at the active operation boundary.
  void exit.catch(() => {});
  child.stdin.on('error', (error) => fail(error));
  child.stdout.on('data', (chunk: Buffer) => {
    if (failure) return;
    receivedBytes += chunk.length;
    if (receivedBytes > 4096) {
      fail(new Error('The data move lease response was invalid.'));
      return;
    }
    buffer += chunk.toString('utf8');
    for (let newline = buffer.indexOf('\n'); newline >= 0; newline = buffer.indexOf('\n')) {
      const line = buffer.slice(0, newline).replace(/\r$/, '');
      buffer = buffer.slice(newline + 1);
      if (!expected || line !== JSON.stringify({ [expected]: true })) {
        fail(new Error('The data move lease response was invalid.'));
        return;
      }
      expected = null;
      clearTimeout(messageTimer);
      const resolve = resolveMessage;
      resolveMessage = null;
      rejectMessage = null;
      resolve?.();
    }
  });
  let stageReleased = false;
  try {
    await ready;
    check();
    const result = await action({
      check,
      beforePromotion: async () => {
        check();
        if (stageReleased) throw new Error('The data staging lease was already released.');
        const released = waitFor('stageReleased');
        child.stdin!.write('{"releaseStage":true}\n');
        await released;
        check();
        stageReleased = true;
      },
    });
    check();
    return result;
  } finally {
    finishing = true;
    clearTimeout(messageTimer);
    child.stdin.end();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        exit,
        new Promise<never>((_resolve, reject) => {
          timer = setTimeout(
            () =>
              reject(
                new Error('The data move lease did not close. The original data was preserved.'),
              ),
            15000,
          );
        }),
      ]);
    } finally {
      clearTimeout(timer);
    }
  }
}
