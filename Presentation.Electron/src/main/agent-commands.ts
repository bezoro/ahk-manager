import { AsyncLocalStorage } from 'node:async_hooks';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import metadataIndex from '../shared/agent-commands.json';
import type {
  ManagerSettings,
  OperationResult,
  ScriptOperation,
  SelectionContext,
} from '../shared/contracts.js';
import { startupRepairFingerprint } from '../shared/startup-repair-validation.js';
import type { UpdateChannel } from '../shared/updates.js';
import {
  activityQuery,
  array,
  assertSettings,
  decimal,
  guid,
  metadata,
  object,
  oneOf,
  operation,
  settings,
  shape,
  text,
  nullable,
  type Validator,
} from '../shared/validation.js';
import type { AgentReply, AgentRequest } from './agent-transport.js';
import type { CommandRegistry } from './command-registry.js';
import type { ManagerController } from './controller.js';
import { assertRequestAllowed, type RequestPolicy } from './ipc/request-policy.js';

type Args = Record<string, unknown>;
type DiagnosticMode = 'recent' | 'errors';

export class AgentCommandError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly outcome: 'Failed' | 'Unknown' = 'Failed',
  ) {
    super(message);
  }
}

class ConfirmationNeeded extends Error {
  constructor(
    readonly title: string,
    readonly detail: string,
    readonly target: unknown,
  ) {
    super(title);
  }
}

interface Confirmation {
  expires: number;
  key: string;
  target: string;
  generation: number;
}

export interface AgentScope {
  alive(): boolean;
  files: string[];
  confirm(title: string, detail: string, target?: unknown): Promise<boolean>;
  updateRelease?: string;
  transition?: string;
}

export interface AgentCommandsBoundary {
  registry: CommandRegistry;
  controller: ManagerController;
  policy(): RequestPolicy;
  status(): unknown;
  ready(): boolean;
  navigation(command: 'show' | 'hide' | 'toggleWindow' | 'preferences'): Promise<AgentReply>;
  shutdown(stopScripts: boolean): Promise<AgentReply>;
  cancelStopExit(): AgentReply;
  checkAdoption(): Promise<AgentReply>;
  continuation(): Promise<unknown>;
  resumeStartup(operationId: string): Promise<AgentReply>;
  update(id: string): Promise<AgentReply>;
  cancelUpdate(id: string): AgentReply;
  prepareStorage(directory?: string): Promise<unknown>;
  restartStorage(): Promise<AgentReply>;
  report(mode: DiagnosticMode): string;
  exportDiagnostics(destination: string): Promise<unknown>;
  receipt(operationId: string): Promise<AgentReply>;
  error(error: unknown): string;
}

export const ok = (result: unknown = null): AgentReply => ({
  outcome: 'Confirmed',
  code: 'Ok',
  result,
});
export const failed = (code: string, result: unknown = null): AgentReply => ({
  outcome: 'Failed',
  code,
  result,
});

export function operationReply(value: OperationResult, result: unknown = value): AgentReply {
  const outcome =
    value.outcome === 'CancelledBeforeDispatch'
      ? 'Failed'
      : value.outcome === 'Pending'
        ? 'Accepted'
        : value.outcome;
  return { outcome, code: value.code, result };
}

const absolute: Validator = (value) =>
  typeof value === 'string' &&
  text()(value) &&
  (/^[a-z]:[\\/]/i.test(value) || /^\\\\[^.?\\/][^\\/]*[\\/][^\\/]+/.test(value));
const nonEmptyPaths: Validator = (value) =>
  array(absolute, 2000)(value) && (value as unknown[]).length > 0;
const none = shape({});
const onePath = shape({ path: absolute });
const row = { scriptId: guid, revision: decimal };
const externalId = shape({ id: text(100) });

function canonical(value: unknown): string {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
  if (object(value))
    return (
      '{' +
      Object.keys(value)
        .sort()
        .map((key) => JSON.stringify(key) + ':' + canonical(value[key]))
        .join(',') +
      '}'
    );
  return JSON.stringify(value) ?? 'null';
}

export async function verifyExecutable(value: string): Promise<void> {
  if (
    !absolute(value) ||
    path.win32.extname(value).toLowerCase() !== '.exe' ||
    !(await fs.stat(value).catch(() => null))?.isFile()
  )
    throw new AgentCommandError('InvalidPath', 'Select an absolute executable file path.');
}

export class AgentCommands {
  readonly scope = new AsyncLocalStorage<AgentScope>();
  private readonly confirmations = new Map<string, Confirmation>();
  private backendGeneration = 0;
  private readonly entries = new Map<
    string,
    {
      validate: Validator;
      policy: string;
      review: boolean;
    }
  >();

  constructor(private readonly host: AgentCommandsBoundary) {
    const { controller, registry } = host;
    const app = (name: string, args: unknown = {}) => registry.invoke(name, args);
    const wire = (name: string) => 'agent:' + name;
    const define = <T = Args>(
      name: string,
      validate: Validator,
      policy: string,
      handler: (args: T) => unknown,
      review = false,
    ) => {
      registry.define(wire(name), validate, handler);
      this.entries.set(name, { validate, policy, review });
    };
    const view = (name: string, internal: string) =>
      define(name, none, internal, () => ok(app(internal)));
    const command = (name: string, internal: string) =>
      define(name, none, internal, async () => ok(await app(internal)));
    const review = (
      name: string,
      validate: Validator,
      policy: string,
      handler: (args: Args) => unknown,
    ) => define(name, validate, policy, handler, true);
    const snapshot = () => {
      if (!controller.state.snapshot || !host.ready())
        throw new AgentCommandError('BackendUnavailable', 'The backend is not ready.');
      return controller.state.snapshot;
    };
    const selection = (args: Args): SelectionContext => ({
      scriptId: String(args.scriptId),
      stateRevision: String(args.revision),
      query: { search: '', status: 'All', sort: 'Name', collectionId: null, version: '0' },
    });
    const stale = () => failed('StaleSelection');
    const result = async (value: unknown) => operationReply((await value) as OperationResult);
    const catalog = async (value: unknown) => {
      const catalog = (await value) as Awaited<ReturnType<typeof controller.add>>;
      return catalog ? operationReply(catalog.result, catalog) : failed('Cancelled');
    };
    const scopedFiles = async (files: string[], directory: boolean) => {
      for (const file of files) {
        const info = await fs.stat(file).catch(() => null);
        if (directory ? !info?.isDirectory() : !info?.isFile())
          throw new AgentCommandError('InvalidPath', 'A supplied path has the wrong file type.');
      }
      this.scope.getStore()!.files = files;
    };
    const targetConfirm = async (title: string, target: unknown) =>
      this.scope.getStore()!.confirm(title, JSON.stringify(target), target);

    define('app.status', none, 'getState', () => ok(host.status()));
    for (const name of ['show', 'hide', 'toggleWindow', 'preferences'] as const)
      define('app.' + name, none, 'getState', () => host.navigation(name));
    define('app.quit', none, 'getState', () => host.shutdown(false));
    review('app.stopScriptsAndQuit', none, 'getState', () => host.shutdown(true));
    define('app.cancelStopExit', none, 'getState', host.cancelStopExit);
    define('app.retryBackend', none, 'retryBackend', async () => {
      await app('retryBackend');
      return host.ready() ? ok(host.status()) : failed('BackendUnavailable', host.status());
    });
    define(
      'commands.preview',
      shape({ command: text(100), arguments: object }),
      'getState',
      () => null,
    );
    define<{ operationId: string }>(
      'operations.get',
      shape({ operationId: guid }),
      'getState',
      ({ operationId }) => host.receipt(operationId),
    );
    view('state.get', 'getState');
    view('theme.get', 'getTheme');
    define('scripts.list', none, 'getState', () => {
      const { revision, scripts } = snapshot();
      return ok({ revision, scripts });
    });
    command('scripts.refresh', 'refresh');
    define<{ paths: string[] }>(
      'scripts.addFiles',
      shape({ paths: nonEmptyPaths }),
      'addFiles',
      async ({ paths }) => {
        await scopedFiles(paths, false);
        return catalog(app('addFiles'));
      },
    );
    define<{ path: string }>('scripts.addFolder', onePath, 'addFolder', async ({ path }) => {
      await scopedFiles([path], true);
      return catalog(app('addFolder'));
    });
    review('scripts.remove', shape(row), 'removeScript', (args) =>
      result(app('removeScript', args)),
    );
    define('scripts.saveMetadata', shape({ ...row, metadata }), 'saveMetadata', (args) =>
      result(app('saveMetadata', args)),
    );
    review('scripts.locate', shape({ ...row, path: absolute }), 'locateScript', async (args) => {
      await scopedFiles([String(args.path)], false);
      return result(controller.locate(selection(args), true));
    });
    review('scripts.recycle', shape(row), 'deleteScriptFile', (args) =>
      result(controller.deleteFile(selection(args), true)),
    );
    review(
      'scripts.operate',
      shape({ ...row, operation: (value) => operation(value) && value !== 'RecycleFile' }),
      'operate',
      (args) =>
        result(controller.operate(args.operation as ScriptOperation, selection(args), true)),
    );
    define('collections.list', none, 'getState', () => {
      const { revision, collections } = snapshot();
      return ok({ revision, collections });
    });
    define(
      'collections.save',
      shape({ collectionId: nullable(guid), name: text(4096) }, { revision: decimal }),
      'saveCollection',
      ({ collectionId, name, revision }) => {
        if (
          collectionId !== null &&
          (snapshot().revision !== revision ||
            !snapshot().collections.some((collection) => collection.id === collectionId))
        )
          return stale();
        return result(
          controller.saveCollection(
            collectionId as string | null,
            String(name),
            revision as string | undefined,
          ),
        );
      },
    );
    review(
      'collections.remove',
      shape({ collectionId: guid, revision: decimal }),
      'removeCollection',
      (args) => result(app('removeCollection', args)),
    );
    define('settings.get', none, 'getState', () => {
      const { revision, settings } = snapshot();
      return ok({ revision, settings });
    });
    review(
      'settings.save',
      shape({ revision: decimal, settings }),
      'saveSettings',
      async (args) => {
        const proposed = assertSettings(args.settings) as ManagerSettings;
        const current = snapshot();
        if (current.revision !== args.revision) return stale();
        for (const key of ['interpreterPath', 'editorPath'] as const) {
          const value = proposed[key];
          if (value && value !== current.settings[key]) await verifyExecutable(value);
        }
        return result(controller.saveSettings(proposed, true, String(args.revision)));
      },
    );
    define<{ path: string }>(
      'settings.validateExecutable',
      onePath,
      'pickEditor',
      async ({ path }) => {
        await verifyExecutable(path);
        return ok({ path });
      },
    );
    command('startup.previewRepair', 'previewStartupRepair');
    define(
      'startup.repair',
      shape({ fingerprint: startupRepairFingerprint }),
      'repairStartup',
      (args) => result(app('repairStartup', args)),
    );
    review('startup.transfer', none, 'transferStartup', () => result(app('transferStartup')));
    define('startup.checkAdoption', none, 'getState', host.checkAdoption);
    define('startup.continuation', none, 'getState', async () => ok(await host.continuation()));
    review(
      'startup.resume',
      shape({ operationId: guid }),
      'requestAdministration',
      ({ operationId }) => host.resumeStartup(String(operationId)),
    );
    define('startup.runLogin', none, 'runLogin', () => result(controller.runLogin()));
    define<{ path: string }>('import.preview', onePath, 'previewImport', async ({ path }) => {
      await scopedFiles([path], false);
      return ok(await app('previewImport'));
    });
    review('import.commit', shape({ token: guid }), 'commitImport', (args) =>
      result(controller.commitImport(String(args.token), true)),
    );
    review('profile.migrate', onePath, 'migrateProfile', async ({ path }) => {
      await scopedFiles([String(path)], true);
      return result(app('migrateProfile'));
    });
    review('profile.restoreBackup', none, 'restoreBackup', () => result(app('restoreBackup')));
    define('profile.recoverPending', none, 'recoverPending', () => result(app('recoverPending')));
    view('storage.get', 'storage:get');
    command('storage.open', 'storage:open');
    review('storage.prepare', onePath, 'storage:choose', async ({ path }) => {
      await targetConfirm('Prepare data folder move', { path, storage: app('storage:get') });
      return ok(await host.prepareStorage(String(path)));
    });
    review('storage.restoreDefault', none, 'storage:restore-default', async () => {
      await targetConfirm('Prepare move to default data folder', app('storage:get'));
      return ok(await host.prepareStorage());
    });
    review('storage.restart', none, 'storage:restart', () => host.restartStorage());
    review('storage.cancel', none, 'storage:cancel', async () => {
      await targetConfirm('Cancel pending data move', app('storage:get'));
      return ok(await app('storage:cancel'));
    });
    view('updates.get', 'getUpdateState');
    define('updates.check', none, 'checkUpdates', async () => {
      const state = (await app('checkUpdates')) as { error?: string; phase: string };
      return state.phase === 'error'
        ? failed('UpdateCheckFailed', state)
        : state.phase === 'idle'
          ? ok(state)
          : failed('OperationInProgress', state);
    });
    define<{ channel: UpdateChannel }>(
      'updates.setChannel',
      shape({ channel: oneOf('stable', 'test') }),
      'setUpdateChannel',
      async ({ channel }) => {
        const state = (await app('setUpdateChannel', { channel })) as {
          error?: string;
          channel: UpdateChannel;
          phase: string;
        };
        if (state.channel !== channel) return failed('UpdateChannelUnchanged', state);
        return state.phase === 'error'
          ? { outcome: 'Partial', code: 'UpdateCheckFailed', result: state }
          : ok(state);
      },
    );
    review('updates.install', externalId, 'installUpdate', ({ id }) => host.update(String(id)));
    define<{ id: string }>(
      'updates.cancelDownload',
      shape({ id: guid }),
      'getUpdateState',
      ({ id }) => host.cancelUpdate(id),
    );
    define('activity.list', activityQuery, 'getActivity', async (args) =>
      ok(await app('getActivity', args)),
    );
    define<{ mode: DiagnosticMode }>(
      'diagnostics.report',
      shape({ mode: oneOf('recent', 'errors') }),
      'copyDiagnostics',
      ({ mode }) => ok({ report: host.report(mode) }),
    );
    review('diagnostics.export', onePath, 'exportDiagnostics', async ({ path }) => {
      const archive = (await host.exportDiagnostics(String(path))) as { complete?: boolean } | null;
      if (!archive) return failed('ExportUnavailable');
      return archive.complete
        ? ok(archive)
        : { outcome: 'Partial', code: 'DiagnosticArchiveIncomplete', result: archive };
    });
    command('diagnostics.openLogs', 'openLogs');
    define('clipboard.write', shape({ text: text(65536) }), 'copyText', async (args) =>
      ok(await app('copyText', args)),
    );
    define('administration.request', none, 'getState', () =>
      controller.state.authorityMode === 'Administrator'
        ? { outcome: 'Confirmed', code: 'AlreadyAdministrator', result: host.status() }
        : failed('ElevationRequired'),
    );
    define('administration.status', none, 'getState', () =>
      ok({
        authorityMode: controller.state.authorityMode,
        pending: controller.state.administrationPending,
      }),
    );

    if (
      this.entries.size !== metadataIndex.length ||
      metadataIndex.some((entry) => !this.entries.has(entry.name))
    )
      throw new Error('The agent command metadata and registry differ.');
  }

  invalidate(): void {
    this.backendGeneration++;
    this.confirmations.clear();
  }

  async dispatch(request: AgentRequest, alive: () => boolean): Promise<AgentReply> {
    const preview = request.command === 'commands.preview';
    const command = preview ? request.arguments?.command : request.command;
    const body = preview ? request.arguments?.arguments : (request.arguments ?? {});
    if (preview && !this.entries.get('commands.preview')!.validate(request.arguments))
      return failed('InvalidArguments');
    const entry = typeof command === 'string' ? this.entries.get(command) : undefined;
    if (!entry || (preview && !entry.review)) return failed('UnknownCommand');
    if (!object(body)) return failed('InvalidArguments');
    const { confirmationToken, ...args } = body;
    if (
      !entry.validate(args) ||
      (confirmationToken !== undefined && (preview || !entry.review || !guid(confirmationToken)))
    )
      return failed('InvalidArguments');
    const key = canonical({ command, args });
    const token = typeof confirmationToken === 'string' ? confirmationToken : '';
    const generation = this.backendGeneration;
    let held: Confirmation | undefined;
    if (entry.review && !preview) {
      held = this.confirmations.get(token);
      if (!held || held.key !== key || held.expires <= Date.now() || held.generation !== generation)
        return failed('ConfirmationRequired');
      this.confirmations.delete(token);
    }
    try {
      assertRequestAllowed(entry.policy, this.host.policy());
    } catch (error) {
      return failed('OperationBlocked', { detail: this.host.error(error) });
    }
    const live = () => alive() && this.backendGeneration === generation;
    const scope: AgentScope = {
      alive: live,
      files: [],
      async confirm(title, detail, target) {
        if (!live())
          throw new AgentCommandError('CommandExpired', 'The command is no longer active.');
        if (preview) throw new ConfirmationNeeded(title, detail, target ?? null);
        if (held?.target !== canonical([title, detail, target ?? null]))
          throw new AgentCommandError(
            'ConfirmationChanged',
            'The reviewed target changed. Preview the command again.',
          );
        return true;
      },
    };
    if (!live()) return failed('CancelledBeforeDispatch');
    try {
      return (await this.scope.run(scope, () =>
        this.host.registry.invoke('agent:' + command, args),
      )) as AgentReply;
    } catch (error) {
      if (error instanceof ConfirmationNeeded && live()) {
        const now = Date.now();
        for (const [key, item] of this.confirmations)
          if (item.expires <= now) this.confirmations.delete(key);
        if (this.confirmations.size >= 64) return failed('ConfirmationCapacity');
        const confirmationToken = randomUUID();
        this.confirmations.set(confirmationToken, {
          key,
          generation,
          expires: now + 300000,
          target: canonical([error.title, error.detail, error.target]),
        });
        return ok({
          confirmationToken,
          title: error.title,
          detail: error.detail,
          target: error.target,
        });
      }
      if (error instanceof AgentCommandError)
        return { outcome: error.outcome, code: error.code, result: { detail: error.message } };
      return {
        outcome: 'Unknown',
        code: 'CommandUnconfirmed',
        result: { detail: this.host.error(error) },
      };
    }
  }
}
