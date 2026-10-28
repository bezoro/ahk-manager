import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn, type ChildProcess } from 'node:child_process';
import { createHash } from 'node:crypto';
import type { CachedInstaller, UpdateStore } from './update-store.js';

const delay = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));
export interface PreparedUpdate { cancel(): Promise<void> }
const incomplete = 'Uma atualização anterior ainda não foi confirmada. Os registros e as cópias foram preservados. Aguarde a conclusão antes de tentar novamente.';

export async function updateRecoveryStatus(store: UpdateStore): Promise<string | null> {
  const root = path.join(store.root, 'operations');
  try { await store.safeDirectory(root); } catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null; throw error; }
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
      if (data.operationId !== name || data.schemaVersion !== 1 || !['Installed', 'RolledBack', 'Failed', 'Unknown'].includes(String(data.outcome))) return incomplete;
      if (data.outcome === 'Unknown') return incomplete;
      const completed = Date.parse(String(data.completedAtUtc));
      if (Number.isFinite(completed) && completed >= latest) {
        latest = completed;
        notice = data.outcome === 'RolledBack' ? 'Uma atualização anterior falhou e a instalação anterior foi restaurada. Seu histórico continua disponível.' : null;
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') return incomplete;
      try { await store.regular(path.join(directory, 'cancel.json'), 4096); continue; } catch { /* A ready operation without a final result must never replay. */ }
      try { await store.regular(path.join(directory, 'ready.json'), 4096); return incomplete; } catch (readyError) { if ((readyError as NodeJS.ErrnoException).code !== 'ENOENT') return incomplete; }
    }
  }
  return notice;
}

/** Pins a copied host outside installation and obtains its native parent-identity acknowledgement. */
export async function prepareUpdate(store: UpdateStore, operationId: string, target: CachedInstaller, installDirectory: string, parentStartedAtUtc: string): Promise<PreparedUpdate> {
  if (!/^[a-f0-9-]{36}$/.test(operationId) || !path.isAbsolute(installDirectory) || !Number.isFinite(Date.parse(parentStartedAtUtc))) throw new Error('Não foi possível identificar a sessão para atualizar.');
  const recovery = await updateRecoveryStatus(store);
  if (recovery === incomplete) throw new Error(recovery);
  await store.verify(target);
  await store.safeDirectory(installDirectory);
  const source = path.join(installDirectory, 'backend', 'update', 'AhkManager.UpdateHost.exe');
  const hash = await store.hash(source);
  const hostDirectory = path.join(store.root, 'host', hash);
  await store.safeDirectory(hostDirectory, true);
  const host = path.join(hostDirectory, 'AhkManager.UpdateHost.exe');
  try { if (await store.hash(host) !== hash) throw new Error('O aplicador de atualizações foi alterado.'); }
  catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; await fs.copyFile(source, host, 1); }
  if (await store.hash(host) !== hash) throw new Error('Não foi possível verificar o aplicador de atualizações.');
  const directory = path.join(store.root, 'operations', operationId);
  await store.safeDirectory(path.dirname(directory), true);
  await fs.mkdir(directory, { mode: 0o700 });
  const plan = { schemaVersion: 1, operationId, parentPid: process.pid, parentStartedAtUtc, installDirectory,
    installerPath: target.installerPath, installerSha256: target.sha256, version: target.version };
  const bytes = Buffer.from(JSON.stringify(plan), 'utf8');
  const planPath = path.join(directory, 'plan.json');
  await store.writeNew(planPath, bytes);
  let child: ChildProcess | null = null;
  const cancel = async (): Promise<void> => {
    try { await store.writeNew(path.join(directory, 'cancel.json'), JSON.stringify({ schemaVersion: 1, operationId })); }
    catch (error) { if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error; }
  };
  try {
    child = spawn(host, ['--plan', planPath, '--sha256', createHash('sha256').update(bytes).digest('hex')], { detached: true, windowsHide: true, stdio: 'ignore', shell: false });
    await new Promise<void>((resolve, reject) => { child!.once('error', reject); child!.once('spawn', resolve); });
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
      if (child.exitCode !== null || child.signalCode !== null) throw new Error('O aplicador recusou a atualização. A instalação foi preservada.');
      try {
        const readyPath = path.join(directory, 'ready.json');
        await store.regular(readyPath, 4096);
        const ready = JSON.parse(await fs.readFile(readyPath, 'utf8')) as Record<string, unknown>;
        if (ready.schemaVersion !== 1 || ready.operationId !== operationId || ready.parentPid !== process.pid || Date.parse(String(ready.parentStartedAtUtc)) !== Date.parse(parentStartedAtUtc)) throw new Error('A confirmação do aplicador não corresponde a esta sessão.');
        child.unref();
        return { cancel };
      } catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
      await delay(100);
    }
    throw new Error('O aplicador não confirmou a preparação no prazo. A instalação foi preservada.');
  } catch (error) { await cancel(); child?.unref(); throw error; }
}
