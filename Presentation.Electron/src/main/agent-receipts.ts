import fs from 'node:fs/promises';
import path from 'node:path';
import { guid, object } from '../shared/validation.js';
import { failed, ok } from './agent-commands.js';
import type { AgentReply } from './agent-transport.js';
import { completedMoveMatches } from './storage/data-migration.js';
import { samePath } from './storage/deployment.js';
import { parseStorageJson } from './storage/json.js';
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
        reopened &&
        dispatch?.operationId === operationId &&
        dispatch.moveId === receipt.moveId &&
        dispatch.generation === receipt.generation &&
        samePath(current.directory, receipt.destination) &&
        (await completedMoveMatches(this.store.root, receipt.moveId, current.directory))
      )
        return ok(result);
    } else if (receipt.kind === 'Update' && typeof receipt.version === 'string') {
      const update = await this.read(
        path.join(this.store.root, 'operations', operationId, 'outcome.json'),
      );
      if (update?.operationId === operationId && update.version === receipt.version) {
        const details = { ...result, worker: update };
        if (
          update.outcome === 'Installed' &&
          update.installerExitCode === 0 &&
          reopened &&
          current.version === receipt.version
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
