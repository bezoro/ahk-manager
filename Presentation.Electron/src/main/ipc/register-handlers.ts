import type { IpcMainEvent, IpcMainInvokeEvent } from 'electron';
import type { AsyncLocalStorage } from 'node:async_hooks';
import type {
  AdministrationUiGuard,
  DiagnosticExportResult,
  OperationResult,
  SelectionContext,
  ThemeState,
} from '../../shared/contracts.js';
import {
  activityQuery,
  assertActivityQuery,
  assertMetadata,
  assertOperation,
  assertSelection,
  assertSettings,
  bool,
  decimal,
  guid,
  metadata,
  nullable,
  oneOf,
  operation,
  requireValid,
  selection,
  shape,
  text,
  type Validator,
} from '../../shared/validation.js';
import { hostText as t } from '../../shared/host-en-US.js';
import type { ManagerController } from '../controller.js';
import type { AdministrationPreflight } from '../elevation.js';
import type { DocumentGuard, DocumentLease } from '../security.js';
import type { UpdateService } from '../updates/update-service.js';
import type { UpdateChannel } from '../../shared/updates.js';
import type { StorageChangeResult, StorageState } from '../../shared/storage.js';
import { assertRequestAllowed, type RequestPolicy } from './request-policy.js';
import { commandDetails, type CommandLogBoundary } from '../logging/command-log.js';

export interface IpcTransport {
  handle(
    channel: string,
    listener: (event: IpcMainInvokeEvent, envelope: unknown) => unknown,
  ): void;
  on(channel: string, listener: (event: IpcMainEvent, envelope: unknown) => void): void;
}

export interface IpcContext {
  commandLog?: Pick<CommandLogBoundary, 'run'>;
  openLogs?(): Promise<void>;
  diagnosticReport?(mode: 'recent' | 'errors'): string;
  exportDiagnostics?(): Promise<DiagnosticExportResult | null>;
  storage?: {
    get(): StorageState;
    choose(): Promise<StorageChangeResult | null>;
    open(): Promise<void>;
    restoreDefault(): Promise<StorageChangeResult>;
    restart(): Promise<void>;
    cancel(): Promise<StorageState>;
  };
  ipc: IpcTransport;
  clipboard: { writeText(text: string): void };
  guard: Pick<DocumentGuard, 'connect' | 'validate' | 'current'>;
  controller: ManagerController;
  updates: Pick<UpdateService, 'snapshot' | 'check' | 'install' | 'setChannel'>;
  preflight: AdministrationPreflight;
  documentScope: AsyncLocalStorage<() => boolean>;
  policy(): RequestPolicy;
  theme(): ThemeState;
  requestAdministration(): Promise<OperationResult>;
  retryBackend(): Promise<void>;
  selectionChanged(selection: SelectionContext | null): void;
  documentReady(lease: DocumentLease): void;
}

/** The renderer reaches application services only through authenticated, validated handlers. */
export function registerHandlers(context: IpcContext): void {
  const { guard, controller, updates, preflight, ipc: ipcMain, clipboard } = context;
  ipcMain.handle('manager:connect', (event) => {
    const connect = () => guard.connect(event);
    return context.commandLog ? context.commandLog.run('ui', 'connect', connect) : connect();
  });
  const none = shape({});

  function bind<T = Record<string, never>>(
    name: string,
    validate: Validator,
    handler: (body: T) => unknown,
  ): void {
    ipcMain.handle(`manager:${name}`, async (event, envelope: unknown) => {
      const dispatch = async () => {
        const validated = guard.validate(event, envelope);
        const body = requireValid<T>(validated.body, validate, `${name} request`);
        assertRequestAllowed(name, context.policy());
        const invoke = () =>
          context.documentScope.run(
            () => guard.current(validated.lease),
            () => handler(body),
          );
        const value = context.commandLog
          ? await context.commandLog.run('ui', 'dispatch.' + name, invoke, commandDetails(body))
          : await invoke();
        if (!guard.current(validated.lease)) throw new Error(t.documentChanged);
        return value;
      };
      return context.commandLog ? context.commandLog.run('ui', name, dispatch) : dispatch();
    });
  }

  bind('getState', none, () => controller.state);
  bind('getTheme', none, context.theme);
  bind('getUpdateState', none, () => updates.snapshot);
  bind('openLogs', none, async () => {
    if (!context.openLogs) throw new Error('Diagnostic logs are unavailable.');
    await context.openLogs();
  });
  bind('exportDiagnostics', none, async () => {
    if (!context.exportDiagnostics) throw new Error('Diagnostic archives are unavailable.');
    return context.exportDiagnostics();
  });
  bind<{ mode: 'recent' | 'errors' }>(
    'copyDiagnostics',
    shape({ mode: oneOf('recent', 'errors') }),
    ({ mode }) => {
      if (!context.diagnosticReport) throw new Error('Diagnostic reports are unavailable.');
      const report = context.diagnosticReport(mode);
      if (!report || Buffer.byteLength(report, 'utf8') > 65536)
        throw new Error('The diagnostic report could not be prepared.');
      if (context.documentScope.getStore()?.() !== true) throw new Error(t.documentChanged);
      clipboard.writeText(report);
    },
  );
  if (context.storage) {
    const storage = context.storage;
    bind('storage:get', none, storage.get);
    bind('storage:choose', none, storage.choose);
    bind('storage:open', none, storage.open);
    bind('storage:restore-default', none, storage.restoreDefault);
    bind('storage:restart', none, storage.restart);
    bind('storage:cancel', none, storage.cancel);
  }
  bind('checkUpdates', none, () => updates.check());
  bind<{ id: string }>('installUpdate', shape({ id: text(100) }), (value) =>
    updates.install(value.id),
  );
  bind<{ channel: UpdateChannel }>(
    'setUpdateChannel',
    shape({ channel: oneOf('stable', 'test') }),
    (value) => updates.setChannel(value.channel),
  );
  bind('requestAdministration', none, () => context.requestAdministration());
  bind('refresh', none, () => controller.refresh());
  bind('addFiles', none, () => controller.add('scripts'));
  bind('addFolder', none, () => controller.add('folder'));
  bind<{ scriptId: string; revision: string }>(
    'removeScript',
    shape({ scriptId: guid, revision: decimal }),
    (value) => controller.remove(value.scriptId, value.revision),
  );
  bind<{ selection: unknown }>('locateScript', shape({ selection }), (value) =>
    controller.locate(assertSelection(value.selection)),
  );
  bind<{ selection: unknown }>('deleteScriptFile', shape({ selection }), (value) =>
    controller.deleteFile(assertSelection(value.selection)),
  );
  bind<{ scriptId: string; metadata: unknown; revision: string }>(
    'saveMetadata',
    shape({ scriptId: guid, metadata, revision: decimal }),
    (value) =>
      controller.saveMetadata(value.scriptId, assertMetadata(value.metadata), value.revision),
  );
  bind<{ collectionId: string | null; name: string }>(
    'saveCollection',
    shape({ collectionId: nullable(guid), name: text(4096) }),
    (value) => controller.saveCollection(value.collectionId, value.name),
  );
  bind<{ collectionId: string; revision: string }>(
    'removeCollection',
    shape({ collectionId: guid, revision: decimal }),
    (value) => controller.removeCollection(value.collectionId, value.revision),
  );
  bind<{ operation: unknown; selection: unknown }>(
    'operate',
    shape({ operation, selection }),
    (value) =>
      controller.operate(assertOperation(value.operation), assertSelection(value.selection)),
  );
  bind<unknown>(
    'saveSettings',
    () => true,
    (value) => controller.saveSettings(assertSettings(value)),
  );
  bind('pickInterpreter', none, () => controller.pick('interpreter'));
  bind('pickEditor', none, () => controller.pick('editor'));
  bind<unknown>('getActivity', activityQuery, (value) =>
    controller.activity(assertActivityQuery(value)),
  );
  bind('previewImport', none, () => controller.previewImport());
  bind<{ token: string }>('commitImport', shape({ token: guid }), (value) =>
    controller.commitImport(value.token),
  );
  bind('migrateProfile', none, () => controller.migrateProfile());
  bind('transferStartup', none, () => controller.transferStartup());
  bind('restoreBackup', none, () => controller.restoreBackup());
  bind('recoverPending', none, () => controller.recoverPending());
  bind('retryBackend', none, context.retryBackend);
  bind<{ text: string }>('copyText', shape({ text: text(65536) }), (value) =>
    clipboard.writeText(value.text),
  );

  function notify(name: string, handler: (event: IpcMainEvent, envelope: unknown) => void): void {
    ipcMain.on(`manager:${name}`, (event, envelope: unknown) => {
      const dispatch = () => handler(event, envelope);
      if (context.commandLog) {
        void context.commandLog.run('ui', name, dispatch).catch(() => {});
      } else {
        try {
          dispatch();
        } catch {
          /* Invalid notifications cannot alter host authority. */
        }
      }
    });
  }

  notify('updateSelection', (event, envelope) => {
    const { body } = guard.validate(event, envelope);
    const selected = body === null ? null : assertSelection(body);
    controller.updateSelection(selected);
    context.selectionChanged(selected);
  });
  notify('documentReady', (event, envelope) => {
    const { lease, body } = guard.validate(event, envelope);
    requireValid(body, none, 'document ready');
    context.documentReady(lease);
  });
  notify('administrationProvider', (event, envelope) => {
    const { body } = guard.validate(event, envelope);
    const value = requireValid<{ registered: boolean }>(
      body,
      shape({ registered: bool }),
      'administration provider',
    );
    preflight.register(value.registered);
  });
  notify('administrationPreflightReply', (event, envelope) => {
    const { body } = guard.validate(event, envelope);
    const value = requireValid<{
      request: { operationId: string; phase: 'begin' | 'commit'; nonce: string };
      value: AdministrationUiGuard | null;
    }>(
      body,
      shape({
        request: shape({ operationId: guid, phase: oneOf('begin', 'commit'), nonce: guid }),
        value: nullable(shape({ dirtyDrafts: bool, dialogOpen: bool, pendingWork: bool })),
      }),
      'administration preflight',
    );
    preflight.reply(value.request, value.value);
  });
}
