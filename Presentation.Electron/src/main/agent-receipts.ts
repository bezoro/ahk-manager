import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { guid, object } from '../shared/validation.js';
import { failed, ok } from './agent-commands.js';
import type { AgentReply } from './agent-transport.js';
import { readDirectoryIdentity, sameDirectoryIdentity } from './storage/data-files.js';
import { completedMoveIdentity } from './storage/data-migration.js';
import { samePath } from './storage/deployment.js';
import { parseStorageJson, storageKeys } from './storage/json.js';
import { UpdateStaging } from './updates/update-staging.js';
import type { UpdateStore } from './updates/update-store.js';

export type AgentTransition =
  { kind: 'Update'; version: string } | { kind: 'DataMove'; moveId: string; destination: string };

export type AgentReceipt = AgentTransition & {
  operationId: string;
  generation: string;
  schemaVersion: 1;
};

export class AgentReceipts {
  constructor(private readonly store: UpdateStore) {}

  private file(operationId: string, suffix: string): string {
    return path.join(this.store.root, 'agent-operations', operationId + suffix + '.json');
  }

  private async create(file: string, data: unknown): Promise<void> {
    await this.store.safeDirectory(path.dirname(file), true);
    await fs.writeFile(file, JSON.stringify(data), { flag: 'wx', flush: true, mode: 0o600 });
  }

  private async read(file: string): Promise<Record<string, unknown> | null> {
    try {
      await this.store.regular(file, 65536);
      const data = parseStorageJson(await fs.readFile(file, 'utf8'));
      if (!object(data) || data.schemaVersion !== 1) throw new Error('Invalid operation receipt.');
      return data;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
      throw error;
    }
  }

  async begin(
    operationId: string,
    generation: string,
    transition: AgentTransition,
  ): Promise<AgentReceipt> {
    const receipt = { schemaVersion: 1 as const, operationId, generation, ...transition };
    await this.create(this.file(operationId, ''), receipt);
    return receipt;
  }

  async markDataMoveDispatch(receipt: AgentReceipt & { kind: 'DataMove' }): Promise<void> {
    await this.create(this.file(receipt.operationId, '-dispatch'), {
      schemaVersion: 1,
      operationId: receipt.operationId,
      moveId: receipt.moveId,
      generation: receipt.generation,
    });
  }

  async settle(
    receipt: AgentReceipt,
    outcome: 'Cancelled' | 'Failed' | 'Unknown',
    detail?: string,
  ): Promise<void> {
    await this.create(this.file(receipt.operationId, '-settled'), {
      schemaVersion: 1,
      operationId: receipt.operationId,
      outcome,
      detail: detail ?? null,
    });
  }

  // The observation records what a ready host verified after reopening. The original
  // receipt and its worker/migration evidence must still match for every later query.
  private async confirmed(
    operationId: string,
    generation: string,
    originalGeneration: string,
    evidence: unknown[],
    observe: () => Promise<boolean>,
  ): Promise<boolean> {
    const directory = path.join(this.store.root, 'agent-operations', operationId + '-confirmed');
    const digest = createHash('sha256').update(JSON.stringify(evidence)).digest('hex');
    const marker = async (): Promise<boolean | null> => {
      try {
        await this.store.safeDirectory(directory);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
        throw error;
      }
      const previous = await this.read(path.join(directory, 'observation.json'));
      return (
        previous !== null &&
        storageKeys(previous, ['schemaVersion', 'operationId', 'generation', 'evidenceSha256']) &&
        previous.operationId === operationId &&
        typeof previous.generation === 'string' &&
        previous.generation.length > 0 &&
        previous.generation !== originalGeneration &&
        previous.evidenceSha256 === digest
      );
    };
    const previous = await marker();
    if (previous !== null) return previous;
    if (!(await observe())) return false;

    let staging: UpdateStaging | undefined;
    try {
      staging = await UpdateStaging.create(directory + '-tmp-', (dir) =>
        this.store.safeDirectory(dir),
      );
      await staging.write(
        'observation.json',
        JSON.stringify({ schemaVersion: 1, operationId, generation, evidenceSha256: digest }),
      );
      await staging.verify();
      // A nonempty directory cannot replace another nonempty directory on Windows or POSIX.
      // The resulting ordinary single-link file can travel with the next data migration.
      await fs.rename(staging.directory, directory);
      return true;
    } catch {
      // An unwritable history store does not invalidate the live proof. A competing
      // publication, if present, must still agree with the original evidence.
      if (staging) await staging.discard().catch(() => {});
      return (await marker()) ?? true;
    }
  }

  async reconcile(
    operationId: string,
    current: {
      generation: string;
      version: string;
      directory: string;
      ready: boolean;
      active: boolean;
    },
  ): Promise<AgentReply> {
    const receipt = await this.read(this.file(operationId, ''));
    if (!receipt || receipt.operationId !== operationId) return failed('OperationNotFound');
    if (typeof receipt.generation !== 'string') throw new Error('Invalid operation generation.');
    const result = { receipt, version: current.version, generation: current.generation };
    const unknown: AgentReply = { outcome: 'Unknown', code: 'OperationUnconfirmed', result };
    const settled = await this.read(this.file(operationId, '-settled'));
    if (settled) {
      if (settled.operationId !== operationId) return unknown;
      const details = { ...result, settlement: settled };
      if (settled.outcome === 'Cancelled') return failed('CancelledBeforeDispatch', details);
      if (settled.outcome === 'Failed') return failed('OperationFailed', details);
      return { ...unknown, result: details };
    }
    const reopened = current.ready && current.generation !== receipt.generation;
    if (
      receipt.kind === 'DataMove' &&
      typeof receipt.moveId === 'string' &&
      guid(receipt.moveId) &&
      typeof receipt.destination === 'string'
    ) {
      const dispatch = await this.read(this.file(operationId, '-dispatch'));
      if (
        dispatch?.operationId === operationId &&
        dispatch.moveId === receipt.moveId &&
        dispatch.generation === receipt.generation
      ) {
        const destination = receipt.destination;
        const identity = completedMoveIdentity(this.store.root, receipt.moveId, destination);
        if (
          identity &&
          (await this.confirmed(
            operationId,
            current.generation,
            receipt.generation,
            [receipt, dispatch, identity],
            async () => {
              if (!reopened || !samePath(current.directory, destination)) return false;
              const live = await readDirectoryIdentity(current.directory).catch(() => null);
              return live !== null && sameDirectoryIdentity(live, identity);
            },
          ))
        )
          return ok(result);
      }
    } else if (receipt.kind === 'Update' && typeof receipt.version === 'string') {
      const update = await this.read(
        path.join(this.store.root, 'operations', operationId, 'outcome.json'),
      );
      if (update?.operationId === operationId && update.version === receipt.version) {
        const details = { ...result, worker: update };
        if (
          update.outcome === 'Installed' &&
          update.installerExitCode === 0 &&
          (await this.confirmed(
            operationId,
            current.generation,
            receipt.generation,
            [receipt, update],
            async () => reopened && current.version === receipt.version,
          ))
        )
          return ok(details);
        if (update.outcome === 'RolledBack' || update.outcome === 'Failed')
          return failed(String(update.outcome), details);
        if (update.outcome === 'Unknown') return { ...unknown, result: details };
      }
    } else {
      throw new Error('Invalid operation type.');
    }
    return current.active ? { outcome: 'Accepted', code: 'OperationInProgress', result } : unknown;
  }
}
