import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import type { DeploymentLayout } from './deployment.js';

type LeaseCommand = '--lease-portable-data-move' | '--lease-portable-storage';
export type StartStorageLease = (helper: string, command: LeaseCommand) => ChildProcess;
const startLease: StartStorageLease = (helper, command) =>
  spawn(helper, [command], {
    shell: false,
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'ignore'],
  });

export interface NativeStorageLease {
  check(): void;
  releaseStage(): Promise<void>;
  close(): Promise<void>;
}

/** The fixed helper derives all protected paths from its authenticated native parent. */
export async function openNativeStorageLease(
  layout: DeploymentLayout,
  command: LeaseCommand,
  start: StartStorageLease = startLease,
  onFailure?: (error: Error) => void,
): Promise<NativeStorageLease> {
  const moving = command === '--lease-portable-data-move';
  const label = moving ? 'data move lease' : 'portable storage lease';
  const preserved = ' The original data was preserved.';
  const child = start(
    path.join(layout.applicationDirectory, 'backend', 'update', 'AhkManager.UpdateHost.exe'),
    command,
  );
  if (!child.stdin || !child.stdout) throw new Error(`The ${label} could not be opened.`);
  let failure: Error | null = null;
  let closed = false;
  let finishing = false;
  let opened = false;
  let buffer = '';
  let receivedBytes = 0;
  let expected: 'ready' | 'stageReleased' | null = null;
  let resolveMessage: (() => void) | null = null;
  let rejectMessage: ((error: Error) => void) | null = null;
  let messageTimer: ReturnType<typeof setTimeout> | undefined;
  const fail = (error: Error): void => {
    if (failure) return;
    failure = error;
    rejectMessage?.(error);
    resolveMessage = null;
    rejectMessage = null;
    clearTimeout(messageTimer);
    if (opened && !finishing) onFailure?.(error);
  };
  const check = (): void => {
    if (failure) throw failure;
    if (closed || child.exitCode !== null) throw new Error(`The ${label} ended.${preserved}`);
  };
  const waitFor = (field: 'ready' | 'stageReleased'): Promise<void> => {
    expected = field;
    return new Promise<void>((resolve, reject) => {
      resolveMessage = resolve;
      rejectMessage = reject;
      messageTimer = setTimeout(
        () => fail(new Error(`The ${label} did not respond.${preserved}`)),
        15000,
      );
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
        const error = new Error(`The ${label} ended unexpectedly.${preserved}`);
        fail(error);
        reject(error);
      } else resolve();
    });
  });
  // A failure before the caller reaches its next check remains observed.
  void exit.catch(() => {});
  child.stdin.on('error', (error) => fail(error));
  child.stdout.on('data', (chunk: Buffer) => {
    if (failure) return;
    receivedBytes += chunk.length;
    if (receivedBytes > 4096) {
      fail(new Error(`The ${label} response was invalid.`));
      return;
    }
    buffer += chunk.toString('utf8');
    for (let newline = buffer.indexOf('\n'); newline >= 0; newline = buffer.indexOf('\n')) {
      const line = buffer.slice(0, newline).replace(/\r$/, '');
      buffer = buffer.slice(newline + 1);
      if (!expected || line !== JSON.stringify({ [expected]: true })) {
        fail(new Error(`The ${label} response was invalid.`));
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
  let closePromise: Promise<void> | null = null;
  const close = (): Promise<void> => {
    if (closePromise) return closePromise;
    finishing = true;
    clearTimeout(messageTimer);
    child.stdin!.end();
    closePromise = (async () => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      try {
        await Promise.race([
          exit,
          new Promise<never>((_resolve, reject) => {
            timer = setTimeout(
              () => reject(new Error(`The ${label} did not close.${preserved}`)),
              15000,
            );
          }),
        ]);
      } finally {
        clearTimeout(timer);
      }
    })();
    return closePromise;
  };
  let stageReleased = false;
  try {
    await ready;
    check();
    opened = true;
    return {
      check,
      close,
      releaseStage: async () => {
        check();
        if (!moving || stageReleased) throw new Error('The data staging lease cannot be released.');
        const released = waitFor('stageReleased');
        child.stdin!.write('{"releaseStage":true}\n');
        await released;
        check();
        stageReleased = true;
      },
    };
  } catch (error) {
    await close().catch(() => {});
    throw error;
  }
}
