import type { IpcMainEvent, IpcMainInvokeEvent } from 'electron';
import type { AsyncLocalStorage } from 'node:async_hooks';
import type {
  AdministrationUiGuard,
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
import { hostText as t } from '../../shared/host-pt-BR.js';
import type { ManagerController } from '../controller.js';
import type { AdministrationPreflight } from '../elevation.js';
import type { DocumentGuard, DocumentLease } from '../security.js';
import type { UpdateService } from '../updates/update-service.js';
import { assertRequestAllowed, type RequestPolicy } from './request-policy.js';

export interface IpcTransport {
  handle(
    channel: string,
    listener: (event: IpcMainInvokeEvent, envelope: unknown) => unknown,
  ): void;
  on(channel: string, listener: (event: IpcMainEvent, envelope: unknown) => void): void;
}

export interface IpcContext {
  ipc: IpcTransport;
  clipboard: { writeText(text: string): void };
  guard: Pick<DocumentGuard, 'connect' | 'validate' | 'current'>;
  controller: ManagerController;
  updates: Pick<UpdateService, 'snapshot' | 'check' | 'install' | 'setToken'>;
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
  ipcMain.handle('manager:connect', (event) => guard.connect(event));
  const none = shape({});

  function bind<T = Record<string, never>>(
    name: string,
    validate: Validator,
    handler: (body: T) => unknown,
  ): void {
    ipcMain.handle(`manager:${name}`, async (event, envelope: unknown) => {
      const validated = guard.validate(event, envelope);
      const body = requireValid<T>(validated.body, validate, `${name} request`);
      assertRequestAllowed(name, context.policy());
      const value = await context.documentScope.run(
        () => guard.current(validated.lease),
        () => handler(body),
      );
      if (!guard.current(validated.lease)) throw new Error(t.documentChanged);
      return value;
    });
  }

  bind('getState', none, () => controller.state);
  bind('getTheme', none, context.theme);
  bind('getUpdateState', none, () => updates.snapshot);
  bind('checkUpdates', none, () => updates.check());
  bind<{ id: string }>('installUpdate', shape({ id: text(100) }), (value) =>
    updates.install(value.id),
  );
  bind<{ token: string | null }>('setUpdateToken', shape({ token: nullable(text(512)) }), (value) =>
    updates.setToken(value.token),
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

  ipcMain.on('manager:updateSelection', (event, envelope: unknown) => {
    try {
      const { body } = guard.validate(event, envelope);
      const selected = body === null ? null : assertSelection(body);
      controller.updateSelection(selected);
      context.selectionChanged(selected);
    } catch {
      /* Invalid notifications cannot alter host authority. */
    }
  });
  ipcMain.on('manager:documentReady', (event, envelope: unknown) => {
    try {
      const { lease, body } = guard.validate(event, envelope);
      requireValid(body, none, 'document ready');
      context.documentReady(lease);
    } catch {
      /* Readiness never bypasses document authentication. */
    }
  });
  ipcMain.on('manager:administrationProvider', (event, envelope: unknown) => {
    try {
      const { body } = guard.validate(event, envelope);
      const value = requireValid<{ registered: boolean }>(
        body,
        shape({ registered: bool }),
        'administration provider',
      );
      preflight.register(value.registered);
    } catch {
      /* An invalid provider cannot enable preflight. */
    }
  });
  ipcMain.on('manager:administrationPreflightReply', (event, envelope: unknown) => {
    try {
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
    } catch {
      /* Obsolete or malformed replies cannot authorize a commit. */
    }
  });
}
