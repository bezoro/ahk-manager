import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn, type ChildProcess } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import type { CachedInstaller, UpdateStore } from './update-store.js';
import { observeUpdateHost, updateHostRefusalMessage } from './update-host-diagnostics.js';

const delay = (milliseconds: number) => new Promise((resolve) => setTimeout(resolve, milliseconds));
export interface PreparedUpdate {
  commit(): Promise<void>;
  cancel(): Promise<void>;
}
const incomplete =
  'A previous update is still unconfirmed. Its records and backups have been preserved. Wait for it to finish before trying again.';

/** A completed backend shutdown publishes one immutable authorization, before the parent exits. */
export function preparedUpdateControl(
  store: UpdateStore,
  operationId: string,
  parentStartedAtUtc: string,
): PreparedUpdate {
  if (
    !/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(operationId) ||
    !Number.isFinite(Date.parse(parentStartedAtUtc))
  )
    throw new Error('The update identity is invalid.');
  const directory = path.join(store.root, 'operations', operationId);
  return {
    async commit(): Promise<void> {
      await store.safeDirectory(directory);
      try {
        await fs.lstat(path.join(directory, 'cancel.json'));
        throw new Error('Update preparation was cancelled.');
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      }
      const bytes = JSON.stringify({
        schemaVersion: 1,
        operationId,
        parentPid: process.pid,
        parentStartedAtUtc,
      });
      const temporary = path.join(directory, `commit-${randomUUID()}.tmp`);
      const file = await fs.open(temporary, 'wx', 0o600);
      try {
        await file.writeFile(bytes, 'utf8');
        await file.sync();
      } finally {
        await file.close();
      }
      // link() publishes complete bytes without replacing an earlier receipt.
      // Remove the temporary link before quitting: the native reader requires
      // a single link and also requires this exact parent to exit with code 0.
      await fs.link(temporary, path.join(directory, 'commit.json'));
      await fs.unlink(temporary);
    },
    async cancel(): Promise<void> {
      try {
        await store.writeNew(
          path.join(directory, 'cancel.json'),
          JSON.stringify({ schemaVersion: 1, operationId }),
        );
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error;
      }
    },
  };
}

export async function updateRecoveryStatus(store: UpdateStore): Promise<string | null> {
  const root = path.join(store.root, 'operations');
  try {
    await store.safeDirectory(root);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
  const names = await fs.readdir(root);
  if (names.length > 1000) return incomplete;
  let notice: string | null = null;
  let latest = 0;
  for (const name of names) {
    if (!/^[a-f0-9-]{36}$/.test(name)) continue;
    const directory = path.join(root, name);
    try {
      await store.safeDirectory(directory);
      const outcome = path.join(directory, 'outcome.json');
      await store.regular(outcome, 65536);
      const data = JSON.parse(await fs.readFile(outcome, 'utf8')) as Record<string, unknown>;
      if (
        data.operationId !== name ||
        data.schemaVersion !== 1 ||
        !['Installed', 'RolledBack', 'Failed', 'Unknown'].includes(String(data.outcome))
      )
        return incomplete;
      if (data.outcome === 'Unknown') return incomplete;
      const completed = Date.parse(String(data.completedAtUtc));
      if (Number.isFinite(completed) && completed >= latest) {
        latest = completed;
        notice =
          data.outcome === 'RolledBack'
            ? 'A previous update failed and the previous installation was restored. Your version history is still available.'
            : null;
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') return incomplete;
      try {
        await store.regular(path.join(directory, 'cancel.json'), 4096);
        continue;
      } catch {
        /* A ready operation without a final result must never replay. */
      }
      try {
        await store.regular(path.join(directory, 'ready.json'), 4096);
        return incomplete;
      } catch (readyError) {
        if ((readyError as NodeJS.ErrnoException).code !== 'ENOENT') return incomplete;
      }
    }
  }
  return notice;
}

/** Pins a copied host outside installation and obtains its native parent-identity acknowledgement. */
export async function prepareUpdate(
  store: UpdateStore,
  operationId: string,
  target: CachedInstaller,
  installDirectory: string,
  parentStartedAtUtc: string,
): Promise<PreparedUpdate> {
  if (
    !/^[a-f0-9-]{36}$/.test(operationId) ||
    !path.isAbsolute(installDirectory) ||
    !Number.isFinite(Date.parse(parentStartedAtUtc))
  )
    throw new Error('The session could not be identified for this update.');
  const recovery = await updateRecoveryStatus(store);
  if (recovery === incomplete) throw new Error(recovery);
  await store.verify(target);
  await store.safeDirectory(installDirectory);
  const source = path.join(installDirectory, 'backend', 'update', 'AhkManager.UpdateHost.exe');
  const hash = await store.hash(source);
  const hostDirectory = path.join(store.root, 'host', hash);
  await store.safeDirectory(hostDirectory, true);
  const host = path.join(hostDirectory, 'AhkManager.UpdateHost.exe');
  try {
    if ((await store.hash(host)) !== hash) throw new Error('The update helper has changed.');
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    await fs.copyFile(source, host, 1);
  }
  if ((await store.hash(host)) !== hash)
    throw new Error('The update helper could not be verified.');
  const directory = path.join(store.root, 'operations', operationId);
  await store.safeDirectory(path.dirname(directory), true);
  await fs.mkdir(directory, { mode: 0o700 });
  const plan = {
    schemaVersion: store.deployment === 'portable' ? 2 : 1,
    ...(store.deployment === 'portable' ? { deployment: 'portable' } : {}),
    operationId,
    parentPid: process.pid,
    parentStartedAtUtc,
    installDirectory,
    installerPath: target.installerPath,
    installerSha256: target.sha256,
    version: target.version,
  };
  const bytes = Buffer.from(JSON.stringify(plan), 'utf8');
  const planPath = path.join(directory, 'plan.json');
  await store.writeNew(planPath, bytes);
  let child: ChildProcess | null = null;
  const control = preparedUpdateControl(store, operationId, parentStartedAtUtc);
  try {
    child = spawn(
      host,
      ['--plan', planPath, '--sha256', createHash('sha256').update(bytes).digest('hex')],
      {
        detached: true,
        windowsHide: true,
        stdio: ['ignore', 'ignore', 'pipe'],
        shell: false,
      },
    );
    const diagnostics = observeUpdateHost(child);
    await new Promise<void>((resolve, reject) => {
      child!.once('error', reject);
      child!.once('spawn', resolve);
    });
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
      if (child.exitCode !== null || child.signalCode !== null) {
        const refusal = await diagnostics.refusal();
        try {
          await store.writeNew(
            path.join(directory, 'helper-refusal.json'),
            JSON.stringify({
              schemaVersion: 1,
              operationId,
              exitCode: child.exitCode,
              refusal,
            }),
          );
        } catch {
          /* Diagnostic persistence cannot replace the original preparation failure. */
        }
        throw new Error(updateHostRefusalMessage(refusal));
      }
      try {
        const readyPath = path.join(directory, 'ready.json');
        await store.regular(readyPath, 4096);
        const ready = JSON.parse(await fs.readFile(readyPath, 'utf8')) as Record<string, unknown>;
        if (
          ready.schemaVersion !== 1 ||
          ready.operationId !== operationId ||
          ready.parentPid !== process.pid ||
          Date.parse(String(ready.parentStartedAtUtc)) !== Date.parse(parentStartedAtUtc)
        )
          throw new Error('The update helper confirmation does not match this session.');
        child.unref();
        return control;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      }
      await delay(100);
    }
    throw new Error(
      'The update helper did not confirm preparation in time. The installation has been preserved.',
    );
  } catch (error) {
    try {
      await control.cancel();
    } finally {
      child?.unref();
    }
    throw error;
  }
}
