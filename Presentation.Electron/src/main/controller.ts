import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type {
  ActivityQuery,
  ActivitySnapshot,
  CatalogResult,
  HostState,
  ImportPreview,
  ManagerSettings,
  ManagerSnapshot,
  OperationResult,
  RecyclePlan,
  RecycleAuthorization,
  ScriptMetadata,
  ScriptOperation,
  SelectionContext,
} from '../shared/contracts.js';
import type { PreparedOperation, ProfileMigrationPreview } from './backend.js';
import { MAX_PENDING } from './backend.js';
import { hostText as t } from '../shared/host-pt-BR.js';

import type { Authority, NativeBoundary } from './controller/boundaries.js';
import { sameProcessIdentity } from './controller/identity.js';
import { blockingScript, cancelled, readySnapshot, stale } from './controller/policies.js';
import { selectionKey, selectionVisible } from './controller/selection.js';
import {
  StopExitCoordinator,
  type StopExitOptions,
  type StopExitReceipt,
} from './controller/stop-exit.js';

export type { Authority, NativeBoundary } from './controller/boundaries.js';
export { sameProcessIdentity } from './controller/identity.js';
export { selectionKey, selectionVisible } from './controller/selection.js';
export type { StopExitItem, StopExitOptions, StopExitReceipt } from './controller/stop-exit.js';

export class ManagerController {
  state: HostState = {
    snapshot: null,
    connection: 'starting',
    error: null,
    busy: false,
    authorityMode: null,
    administrationPending: false,
  };
  private tail: Promise<unknown> = Promise.resolve();
  private queued = 0;
  private selection: SelectionContext | null = null;
  private selectionEpoch = 0;
  private paths = new Set<string>();
  private previews = new Map<
    string,
    { previewId: string; preview: ImportPreview; expires: number }
  >();
  private readonly stopExit: StopExitCoordinator;
  constructor(
    private authority: Authority,
    private readonly native: NativeBoundary,
    private readonly publish: (state: HostState) => void,
  ) {
    this.stopExit = new StopExitCoordinator({
      state: () => this.state,
      authority: () => this.authority,
      setSnapshot: (snapshot) => this.setSnapshot(snapshot),
      drain: () => this.drain(),
      native: this.native,
      activeChanged: (active) => {
        this.state = { ...this.state, busy: this.queued > 0 || active };
        this.publish(this.state);
      },
    });
  }
  setAuthority(authority: Authority): void {
    this.authority = authority;
    this.paths.clear();
    this.previews.clear();
    this.updateSelection(null);
  }
  setSnapshot(snapshot: ManagerSnapshot): void {
    this.state = { ...this.state, snapshot };
    this.publish(this.state);
  }
  setHostAuthority(authorityMode: HostState['authorityMode']): void {
    this.state = { ...this.state, authorityMode };
    this.publish(this.state);
  }
  setAdministrationPending(administrationPending: boolean): void {
    this.state = { ...this.state, administrationPending };
    this.publish(this.state);
  }
  setConnection(connection: HostState['connection'], error: string | null = null): void {
    this.state = { ...this.state, connection, error };
    if (connection !== 'ready') {
      this.previews.clear();
      this.updateSelection(null);
    }
    this.publish(this.state);
  }
  updateSelection(value: SelectionContext | null): void {
    if (selectionKey(value) !== selectionKey(this.selection)) this.selectionEpoch++;
    this.selection = value;
  }
  private requireReady(): ManagerSnapshot {
    return readySnapshot(this.state);
  }
  private matches(revision: string): boolean {
    return this.requireReady().revision === revision;
  }
  private async serialize<T>(work: () => Promise<T>): Promise<T> {
    this.requireReady();
    if (this.state.administrationPending) throw new Error(t.administrationPending);
    if (this.stopExit.active) throw new Error(t.stopExitBusy);
    if (this.queued >= MAX_PENDING) throw new Error(t.queueFull);
    this.queued++;
    this.state = { ...this.state, busy: true };
    this.publish(this.state);
    const task = this.tail.then(() => {
      this.requireReady();
      if (!this.native.alive()) throw new Error(t.documentClosed);
      return work();
    });
    this.tail = task.catch(() => undefined);
    return task.finally(() => {
      this.queued--;
      this.state = { ...this.state, busy: this.queued > 0 || this.stopExit.active };
      this.publish(this.state);
    });
  }
  async refresh(): Promise<void> {
    this.requireReady();
    this.setSnapshot(await this.authority.request<ManagerSnapshot>('refresh', {}));
  }
  async runLogin(): Promise<OperationResult> {
    return this.serialize(() => this.authority.request('runLogin', {}));
  }
  async add(kind: 'scripts' | 'folder'): Promise<CatalogResult | null> {
    return this.serialize(async () => {
      const paths = await this.native.files(kind);
      if (!paths.length || !this.native.alive()) return null;
      return kind === 'scripts'
        ? this.authority.request('addFiles', { paths })
        : this.authority.request('addFolder', { path: paths[0] });
    });
  }
  async remove(scriptId: string, revision: string): Promise<OperationResult> {
    return this.serialize(async () => {
      if (!this.matches(revision)) return stale();
      const script = this.state.snapshot!.scripts.find((s) => s.id === scriptId);
      if (!script) return stale();
      if (
        !(await this.native.confirm(
          t.removeTitle,
          t.removeDetail(script.friendlyName, script.path),
        ))
      )
        return cancelled();
      if (!this.native.alive() || !this.matches(revision)) return stale();
      return this.authority.request('remove', { scriptId });
    });
  }
  async saveMetadata(
    scriptId: string,
    metadata: ScriptMetadata,
    revision: string,
  ): Promise<OperationResult> {
    return this.serialize(async () => {
      if (!this.matches(revision)) return stale();
      const script = this.state.snapshot!.scripts.find((s) => s.id === scriptId);
      if (!script) return stale();
      let startupConsent = false;
      if (metadata.runAtLogin === true && script.runAtLogin !== true) {
        startupConsent = await this.native.confirm(
          t.scriptStartupTitle,
          t.scriptStartupDetail(script.friendlyName, script.path),
        );
        if (!startupConsent) return cancelled();
      }
      if (!this.native.alive() || !this.matches(revision)) return stale();
      return this.authority.request('saveMetadata', { scriptId, metadata, startupConsent });
    });
  }
  async locate(selection: SelectionContext): Promise<OperationResult> {
    const epoch = this.selectionEpoch;
    return this.serialize(async () => {
      const valid = () =>
        this.native.alive() &&
        epoch === this.selectionEpoch &&
        selectionKey(selection) === selectionKey(this.selection) &&
        this.matches(selection.stateRevision);
      if (!valid()) return stale();
      const script = this.requireReady().scripts.find((s) => s.id === selection.scriptId);
      if (
        !script ||
        this.requireReady().readOnly ||
        !selectionVisible(this.requireReady(), selection) ||
        !(script.status === 'Missing' || script.issues.some((i) => i.code === 'MissingFile'))
      )
        return stale();
      const paths = await this.native.files('locate');
      if (!paths.length) return cancelled();
      if (paths.length !== 1 || !valid()) return stale();
      let startupConsent = false;
      if (script.runAtLogin === true) {
        startupConsent = await this.native.confirm(
          t.locateStartupTitle,
          t.locateStartupDetail(paths[0]!),
        );
        if (!startupConsent) return cancelled();
      }
      if (!valid()) return stale();
      return this.authority.request('relink', {
        selection,
        oldPath: script.path,
        replacementPath: paths[0],
        startupConsent,
      });
    });
  }
  async deleteFile(selection: SelectionContext): Promise<OperationResult> {
    const epoch = this.selectionEpoch;
    return this.serialize(async () => {
      const authority = this.authority;
      const valid = () =>
        this.native.alive() &&
        authority === this.authority &&
        epoch === this.selectionEpoch &&
        selectionKey(selection) === selectionKey(this.selection) &&
        selectionVisible(this.requireReady(), selection);
      if (!valid() || !this.matches(selection.stateRevision) || this.requireReady().readOnly)
        return stale();
      if (!this.native.fileStamp || !this.native.trash)
        return {
          outcome: 'Failed',
          code: 'CapabilityUnverified',
          detail: null,
          correlationId: null,
        };
      const plan = await authority.request<RecyclePlan>('prepareRecycle', { selection });
      if (!plan.allowed || !plan.file)
        return {
          outcome: 'Failed',
          code: plan.reason,
          detail: null,
          correlationId: plan.operationId,
        };
      if (!valid() || !this.matches(selection.stateRevision)) return stale();
      const stamp = await this.native.fileStamp(plan.file);
      if (
        !(await this.native.confirm(
          t.deleteTitle,
          t.deleteDetail(plan.path, plan.expectedIdentity?.pid ?? null),
        ))
      )
        return cancelled();
      if (!valid() || !this.matches(selection.stateRevision)) return stale();
      const authorization = await authority.request<RecycleAuthorization>('authorizeRecycle', {
        operationId: plan.operationId,
        selection,
        confirmed: true,
      });
      if (authorization.result.outcome !== 'Pending' || !authorization.file)
        return authorization.result;
      const dispatchValid = () => {
        const current = this.state.snapshot;
        const target = current?.scripts.find((script) => script.id === plan.scriptId);
        return (
          this.native.alive() &&
          authority === this.authority &&
          epoch === this.selectionEpoch &&
          selectionKey(selection) === selectionKey(this.selection) &&
          this.state.connection === 'ready' &&
          !!current &&
          !current.readOnly &&
          current.effectsAvailable &&
          !!target &&
          target.path.toLowerCase() === plan.path.toLowerCase() &&
          (!target.process ||
            sameProcessIdentity(target.process.identity, plan.expectedIdentity)) &&
          !(
            target.status === 'Unknown' &&
            !target.issues.some((issue) => issue.code === 'PendingOperation')
          ) &&
          !target.issues.some(
            (issue) => blockingScript.has(issue.code) && issue.code !== 'PendingOperation',
          )
        );
      };
      const complete = (
        receipt: 'Confirmed' | 'Failed' | 'Unknown',
        detail: string | null,
      ): Promise<OperationResult> =>
        authority.request('completeRecycle', { operationId: plan.operationId, receipt, detail });
      const unknown = (): OperationResult => ({
        outcome: 'Unknown',
        code: 'PendingOperation',
        detail: (authorization.stopped ? t.recycleStopped : '') + t.recycleUnknown,
        correlationId: plan.operationId,
      });
      try {
        if (!dispatchValid()) return await complete('Failed', t.recycleFailed);
        const finalStamp = await this.native.fileStamp(authorization.file);
        if (finalStamp !== stamp || !dispatchValid())
          return await complete('Failed', t.recycleFailed);
      } catch (error) {
        return complete('Failed', error instanceof Error ? error.message : t.recycleFailed).catch(
          unknown,
        );
      }
      // Keep this handler alive independently of the renderer and report only this operation ID.
      const authorizedPath = authorization.file.path;
      const authorizedFile = authorization.file;
      const nativeFailure = async () => {
        try {
          if ((await this.native.fileStamp!(authorizedFile)) === stamp)
            return await complete('Failed', t.recycleFailed);
        } catch {}
        return complete('Unknown', t.recycleUnknown);
      };
      const completion = Promise.resolve()
        .then(() => {
          if (!dispatchValid()) return complete('Failed', t.recycleFailed);
          return this.native.trash!(authorizedPath).then(
            () => complete('Confirmed', null),
            nativeFailure,
          );
        })
        .catch(unknown);
      let timer: ReturnType<typeof setTimeout> | undefined;
      const timeout = new Promise<OperationResult>((resolve) => {
        timer = setTimeout(() => {
          resolve(unknown());
          void Promise.resolve()
            .then(() => complete('Unknown', t.recycleUnknown))
            .catch(() => undefined);
        }, 30000);
      });
      try {
        return await Promise.race([completion, timeout]);
      } finally {
        clearTimeout(timer);
      }
    });
  }
  async saveCollection(collectionId: string | null, name: string): Promise<OperationResult> {
    return this.serialize(() => this.authority.request('saveCollection', { collectionId, name }));
  }
  async removeCollection(collectionId: string, revision: string): Promise<OperationResult> {
    return this.serialize(async () => {
      if (!this.matches(revision)) return stale();
      const collection = this.state.snapshot!.collections.find((c) => c.id === collectionId);
      if (!collection) return stale();
      if (
        !(await this.native.confirm(
          t.removeCollectionTitle,
          t.removeCollectionDetail(collection.name),
        ))
      )
        return cancelled();
      if (!this.native.alive() || !this.matches(revision)) return stale();
      return this.authority.request('removeCollection', { collectionId });
    });
  }
  async operate(operation: ScriptOperation, selection: SelectionContext): Promise<OperationResult> {
    const requestedEpoch = this.selectionEpoch;
    return this.serialize(async () => {
      const valid = () =>
        this.native.alive() &&
        this.selectionEpoch === requestedEpoch &&
        selectionKey(selection) === selectionKey(this.selection) &&
        selectionVisible(this.requireReady(), selection);
      if (!valid() || !this.matches(selection.stateRevision)) return stale();
      const prepared = await this.authority.request<PreparedOperation>('prepare', {
        selection,
        operation,
      });
      if (!prepared.plan.allowed)
        return {
          outcome: 'Failed',
          code: prepared.plan.reason,
          detail: null,
          correlationId: prepared.plan.correlationId,
        };
      if (!valid()) return stale();
      const target = this.state.snapshot!.scripts.find((s) => s.id === selection.scriptId)!;
      let confirmed = false;
      if (prepared.plan.requiresConfirmation) {
        confirmed = await this.native.confirm(
          t.operationTitle,
          t.operationDetail(
            t.operations[operation],
            target.friendlyName,
            target.path,
            target.process?.identity.pid ?? null,
          ),
        );
        if (!confirmed) return cancelled();
      }
      if (!valid()) return stale();
      // A later authoritative revision is supplied; backend rejects a stale plan itself.
      const currentSelection = { ...selection, stateRevision: this.state.snapshot!.revision };
      return this.authority.request('perform', {
        planId: prepared.planId,
        currentSelection,
        confirmed,
      });
    });
  }
  async pick(kind: 'interpreter' | 'editor'): Promise<string | null> {
    if (this.stopExit.active) throw new Error(t.stopExitBusy);
    const values = await this.native.files(kind);
    const value = values[0] ?? null;
    if (value && this.native.alive()) this.paths.add(path.normalize(value).toLowerCase());
    return this.native.alive() ? value : null;
  }
  async saveSettings(proposed: ManagerSettings): Promise<OperationResult> {
    return this.serialize(async () => {
      const current = this.requireReady();
      for (const key of ['interpreterPath', 'editorPath'] as const) {
        const value = proposed[key];
        if (
          value !== null &&
          value !== current.settings[key] &&
          !this.paths.has(path.normalize(value).toLowerCase())
        )
          throw new Error(t.selectExecutable);
      }
      let startupConsent = false;
      if (
        proposed.launchManagerAtLogin &&
        (!current.settings.launchManagerAtLogin ||
          proposed.launchManagerAsAdministrator !== current.settings.launchManagerAsAdministrator)
      ) {
        startupConsent = await this.native.confirm(t.managerStartupTitle, t.managerStartupDetail);
        if (!startupConsent) return cancelled();
      }
      if (!this.native.alive() || !this.matches(current.revision)) return stale();
      return this.authority.request('saveSettings', {
        settings: { ...proposed, window: this.native.placement() },
        startupConsent,
      });
    });
  }
  async activity(query: ActivityQuery): Promise<ActivitySnapshot[]> {
    this.requireReady();
    return this.authority.request('activity', { query });
  }
  async previewImport(): Promise<{ token: string; preview: ImportPreview } | null> {
    return this.serialize(async () => {
      const paths = await this.native.files('import');
      if (!paths.length || !this.native.alive()) return null;
      const result = await this.authority.request<{ previewId: string; preview: ImportPreview }>(
        'previewImport',
        { sourcePath: paths[0] },
      );
      for (const [key, value] of this.previews)
        if (value.expires < Date.now()) this.previews.delete(key);
      if (this.previews.size >= 32) throw new Error(t.previewsFull);
      const token = randomUUID();
      this.previews.set(token, { ...result, expires: Date.now() + 300000 });
      return { token, preview: result.preview };
    });
  }
  async commitImport(token: string): Promise<OperationResult> {
    return this.serialize(async () => {
      const held = this.previews.get(token);
      this.previews.delete(token);
      if (!held || held.expires < Date.now() || !held.preview.canImport) return stale();
      if (
        !(await this.native.confirm(
          t.importTitle,
          t.importDetail(held.preview.sourcePath, held.preview.scriptCount),
        ))
      )
        return cancelled();
      if (!this.native.alive()) return stale();
      return this.authority.request('import', { previewId: held.previewId });
    });
  }
  async migrateProfile(beforeLoad = false): Promise<OperationResult | null> {
    const migrate = async (): Promise<OperationResult | null> => {
      const paths = await this.native.files('migration');
      if (!paths.length || !this.native.alive()) return null;
      const preview = await this.authority.request<ProfileMigrationPreview>('previewMigration', {
        sourceRoot: paths[0],
      });
      if (!preview.canMigrate)
        return {
          outcome: 'Failed',
          code: preview.issues[0]?.code ?? 'ImportInvalid',
          detail: preview.sourceRoot,
          correlationId: null,
        };
      const confirmed = await this.native.confirm(
        t.migrationTitle,
        t.migrationDetail(preview.sourceRoot, preview.fileCount, preview.pendingOperations),
      );
      if (!confirmed) return cancelled();
      if (!this.native.alive()) return stale();
      return this.authority.request('migrate', { previewId: preview.previewId, confirmed: true });
    };
    return beforeLoad ? migrate() : this.serialize(migrate);
  }
  async transferStartup(): Promise<OperationResult> {
    return this.serialize(async () => {
      const preview = await this.authority.request<{
        previewId: string;
        oldExecutable: string | null;
        newExecutable: string;
        oldCommand: string | null;
        newCommand: string;
        canTransfer: boolean;
        reason: string;
      }>('previewStartupTransfer', {});
      if (!preview.canTransfer)
        return {
          outcome: 'Failed',
          code: preview.reason,
          detail: preview.oldExecutable,
          correlationId: null,
        };
      const confirmed = await this.native.confirm(
        t.transferTitle,
        t.transferDetail(preview.oldExecutable, preview.newExecutable),
      );
      if (!this.native.alive()) return stale();
      return this.authority.request('transferStartup', { previewId: preview.previewId, confirmed });
    });
  }
  async restoreBackup(): Promise<OperationResult> {
    return this.serialize(async () => {
      if (!(await this.native.confirm(t.restoreTitle, t.restoreDetail))) return cancelled();
      if (!this.native.alive()) return stale();
      return this.authority.request('restoreBackup', {});
    });
  }
  async recoverPending(): Promise<OperationResult> {
    return this.serialize(() => this.authority.request('recoverPending', {}));
  }
  async savePlacement(administrative = false): Promise<void> {
    if (!this.state.snapshot || !['ready', 'closing'].includes(this.state.connection)) return;
    const save = async () => {
      if (this.state.snapshot)
        await this.authority.request('saveSettings', {
          settings: { ...this.state.snapshot.settings, window: this.native.placement() },
          startupConsent: false,
        });
    };
    if (administrative) {
      if (!this.state.administrationPending || this.queued)
        throw new Error(t.administrationPending);
      await save();
    } else if (this.state.connection === 'closing') await save();
    else await this.serialize(save);
  }
  async drain(): Promise<void> {
    await this.tail;
  }

  /** Native host only; admission and busy state remain coordinated with this queue. */
  async stopScriptsForExit(options: StopExitOptions): Promise<StopExitReceipt> {
    return this.stopExit.run(options);
  }
}
