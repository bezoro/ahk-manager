import { described, jsonSchema } from './json-schema.js';
import { startupRepairFingerprint } from './startup-repair-validation.js';
import {
  activityQuery,
  array,
  decimal,
  guid,
  metadata,
  nullable,
  object,
  oneOf,
  operationNames,
  settings,
  shape,
  text,
  type Validated,
} from './validation.js';

const drivePath = /^[a-zA-Z]:[\\/]/;
const uncPath = /^\\\\[^.?\\/][^\\/]*[\\/][^\\/]+/;
const pathText = text();
export const absoluteAgentPath = described(
  (value: unknown): value is string =>
    pathText(value) && (drivePath.test(value) || uncPath.test(value)),
  () => ({
    ...jsonSchema(pathText),
    anyOf: [{ pattern: drivePath.source }, { pattern: uncPath.source }],
    description: 'Absolute Windows path.',
  }),
);

const none = shape({});
const onePath = shape({ path: absoluteAgentPath });
const operationId = shape({ operationId: guid });
const row = { scriptId: guid, revision: decimal };
const scriptOperation = oneOf(...operationNames.filter((name) => name !== 'RecycleFile'));

// These validators admit command arguments before transport-specific confirmation fields.
export const agentCommandSchemas = {
  'app.start': none,
  'app.status': none,
  'app.show': none,
  'app.hide': none,
  'app.toggleWindow': none,
  'app.preferences': none,
  'app.quit': none,
  'app.stopScriptsAndQuit': none,
  'app.cancelStopExit': none,
  'app.retryBackend': none,
  'commands.preview': shape({ command: text(100), arguments: object }),
  'operations.get': operationId,
  'state.get': none,
  'theme.get': none,
  'scripts.list': none,
  'scripts.refresh': none,
  'scripts.addFiles': shape({ paths: array(absoluteAgentPath, 2000, 1) }),
  'scripts.addFolder': onePath,
  'scripts.remove': shape(row),
  'scripts.saveMetadata': shape({ ...row, metadata }),
  'scripts.locate': shape({ ...row, path: absoluteAgentPath }),
  'scripts.recycle': shape(row),
  'scripts.operate': shape({ ...row, operation: scriptOperation }),
  'collections.list': none,
  'collections.save': shape(
    { collectionId: nullable(guid), name: text(4096) },
    { revision: decimal },
  ),
  'collections.remove': shape({ collectionId: guid, revision: decimal }),
  'settings.get': none,
  'settings.save': shape({ revision: decimal, settings }),
  'settings.validateExecutable': onePath,
  'startup.previewRepair': none,
  'startup.repair': shape({ fingerprint: startupRepairFingerprint }),
  'startup.transfer': none,
  'startup.checkAdoption': none,
  'startup.continuation': none,
  'startup.resume': operationId,
  'startup.runLogin': none,
  'import.preview': onePath,
  'import.commit': shape({ token: guid }),
  'profile.migrate': onePath,
  'profile.restoreBackup': none,
  'profile.recoverPending': none,
  'storage.get': none,
  'storage.open': none,
  'storage.prepare': onePath,
  'storage.restoreDefault': none,
  'storage.restart': none,
  'storage.cancel': none,
  'updates.get': none,
  'updates.check': none,
  'updates.setChannel': shape({ channel: oneOf('stable', 'test') }),
  'updates.install': shape({ id: text(100) }),
  'updates.installLatest': none,
  'updates.cancelDownload': shape({ id: guid }),
  'activity.list': activityQuery,
  'diagnostics.report': shape({ mode: oneOf('recent', 'errors') }),
  'diagnostics.export': onePath,
  'diagnostics.openLogs': none,
  'clipboard.write': shape({ text: text(65536) }),
  'administration.request': none,
  'administration.status': none,
};

export type AgentCommandName = keyof typeof agentCommandSchemas;
export type HostCommandName = Exclude<AgentCommandName, 'app.start'>;
export type AgentCommandArgs<Name extends AgentCommandName> = Validated<
  (typeof agentCommandSchemas)[Name]
>;
