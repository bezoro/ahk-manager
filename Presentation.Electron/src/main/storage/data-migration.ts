import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { StorageChangeResult, StorageState } from '../../shared/storage.js';
import {
  assertStoragePath,
  containsPath,
  parsePortableConfiguration,
  portableApplicationId,
  portableMoveName,
  readStorageJson,
  samePath,
  validateDataDirectory,
  type DeploymentLayout,
} from './deployment.js';
import { storageKeys, storageObject } from './json.js';
import type { DataMoveBoundary } from './data-move-lease.js';
import {
  readDirectoryIdentity,
  sameDirectoryIdentity,
  dataDirectoryExists,
  assertEmptyDirectory,
  inventoryData,
  fingerprintData,
  copyDataInventory,
  type DirectoryIdentity,
} from './data-files.js';

interface PendingMove {
  schemaVersion: 1;
  operationId: string;
  sourceDataDirectory: string;
  destinationDataDirectory: string;
  stagingDirectory: string;
  createdAtUtc: string;
  sourceIdentity: DirectoryIdentity;
  stageIdentity: DirectoryIdentity;
  destinationIdentity: DirectoryIdentity | null;
}
const guid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';
const fail = (message: string): Error => new Error(message);

function configurationBytes(portableRoot: string, destination: string): string {
  const relative = path.relative(portableRoot, destination);
  const setting =
    relative &&
    !relative.startsWith('..' + path.sep) &&
    relative !== '..' &&
    !path.isAbsolute(relative)
      ? relative
      : destination;
  const bytes = JSON.stringify({
    schemaVersion: 1,
    applicationId: portableApplicationId,
    dataDirectory: setting,
  });
  if (Buffer.byteLength(bytes, 'utf8') > 4096)
    throw fail('The selected data folder path is too long. Choose a shorter path.');
  return bytes;
}

function requirePortable(layout: DeploymentLayout): asserts layout is DeploymentLayout & {
  portableRoot: string;
  configurationPath: string;
} {
  if (layout.mode !== 'portable' || !layout.portableRoot || !layout.configurationPath)
    throw fail('The data location can be changed in the portable edition.');
}

function directoryIdentity(value: unknown): value is DirectoryIdentity {
  return (
    storageObject(value) &&
    storageKeys(value, ['dev', 'ino', 'birthtimeNs']) &&
    ['dev', 'ino', 'birthtimeNs'].every(
      (key) => typeof value[key] === 'string' && /^\d{1,30}$/.test(value[key]),
    )
  );
}

function readPending(layout: DeploymentLayout): PendingMove | null {
  requirePortable(layout);
  let value: unknown;
  try {
    value = readStorageJson(path.join(layout.portableRoot, portableMoveName), 16384);
  } catch (error) {
    if (missing(error)) return null;
    throw error;
  }
  const fields = [
    'schemaVersion',
    'operationId',
    'sourceDataDirectory',
    'destinationDataDirectory',
    'stagingDirectory',
    'createdAtUtc',
    'sourceIdentity',
    'stageIdentity',
    'destinationIdentity',
  ];
  if (
    !storageObject(value) ||
    !storageKeys(value, fields) ||
    value.schemaVersion !== 1 ||
    typeof value.operationId !== 'string' ||
    !guid.test(value.operationId) ||
    typeof value.sourceDataDirectory !== 'string' ||
    typeof value.destinationDataDirectory !== 'string' ||
    typeof value.stagingDirectory !== 'string' ||
    typeof value.createdAtUtc !== 'string' ||
    !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value.createdAtUtc) ||
    !Number.isFinite(Date.parse(value.createdAtUtc)) ||
    !directoryIdentity(value.sourceIdentity) ||
    !directoryIdentity(value.stageIdentity) ||
    (value.destinationIdentity !== null && !directoryIdentity(value.destinationIdentity))
  )
    throw fail('The pending data move is invalid. The original data was preserved.');
  const pending = value as unknown as PendingMove;
  for (const directory of [pending.sourceDataDirectory, pending.destinationDataDirectory]) {
    if (
      !path.isAbsolute(directory) ||
      validateDataDirectory(layout.portableRoot, directory) !== directory
    )
      throw fail('The pending data move has an invalid location.');
  }
  if (
    containsPath(pending.sourceDataDirectory, pending.destinationDataDirectory) ||
    containsPath(pending.destinationDataDirectory, pending.sourceDataDirectory)
  )
    throw fail('The source and destination data folders overlap.');
  const expectedStage = path.join(
    path.dirname(pending.destinationDataDirectory),
    `.${path.basename(pending.destinationDataDirectory)}.ahk-manager-move-${pending.operationId}`,
  );
  if (pending.stagingDirectory !== expectedStage)
    throw fail('The pending data staging folder is invalid.');
  assertStoragePath(pending.stagingDirectory);
  if (
    !samePath(layout.dataDirectory, pending.sourceDataDirectory) &&
    !samePath(layout.dataDirectory, pending.destinationDataDirectory)
  )
    throw fail('The data location changed after this move was prepared.');
  return pending;
}

export function getStorageState(
  layout: DeploymentLayout,
  error: string | null = null,
): StorageState {
  const pending = layout.mode === 'portable' ? readPending(layout) : null;
  return {
    mode: layout.mode,
    currentDataDirectory: layout.dataDirectory,
    defaultDataDirectory: layout.defaultDataDirectory,
    pendingDataDirectory: pending?.destinationDataDirectory ?? null,
    canChange: layout.mode === 'portable' && pending === null,
    error,
  };
}

/** Refuse moving unfinished recovery authority into a different storage root. */
async function assertUpdatesSettled(dataDirectory: string): Promise<void> {
  const operations = path.join(dataDirectory, 'Updates', 'operations');
  if (!(await dataDirectoryExists(operations))) return;
  assertStoragePath(operations);
  const entries = await fs.readdir(operations, { withFileTypes: true });
  if (entries.length > 1000)
    throw fail('The update recovery history exceeds its inspection limit.');
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.isSymbolicLink() || !guid.test(entry.name))
      throw fail('The update recovery history could not be verified.');
    const operation = path.join(operations, entry.name);
    const outcomePath = path.join(operation, 'outcome.json');
    if (await dataDirectoryExists(outcomePath)) {
      const outcome = readStorageJson(outcomePath, 65536);
      if (
        storageObject(outcome) &&
        outcome.schemaVersion === 1 &&
        outcome.operationId === entry.name &&
        ['Installed', 'RolledBack', 'Failed'].includes(String(outcome.outcome))
      )
        continue;
      throw fail('Resolve the unfinished update before moving the data folder.');
    }
    const cancelPath = path.join(operation, 'cancel.json');
    if (await dataDirectoryExists(cancelPath)) {
      const cancel = readStorageJson(cancelPath, 4096);
      if (
        storageObject(cancel) &&
        storageKeys(cancel, ['schemaVersion', 'operationId']) &&
        cancel.schemaVersion === 1 &&
        cancel.operationId === entry.name &&
        !(await dataDirectoryExists(path.join(operation, 'ready.json')))
      )
        continue;
    }
    throw fail('Resolve the unfinished update before moving the data folder.');
  }
}

/** Prepare intent only. Operational files can still change until the normal restart. */
export async function prepareDataMove(
  layout: DeploymentLayout,
  requestedDirectory: string,
  preparePrivateDirectory: (directory: string) => Promise<void>,
): Promise<StorageChangeResult> {
  requirePortable(layout);
  const destination = validateDataDirectory(layout.portableRoot, requestedDirectory);
  if (samePath(destination, layout.dataDirectory))
    return { state: getStorageState(layout), restartRequired: false };
  if (readPending(layout))
    throw fail('A data move is already prepared. Restart Manager or cancel that move first.');
  if (
    containsPath(layout.dataDirectory, destination) ||
    containsPath(destination, layout.dataDirectory)
  )
    throw fail('Choose a data folder outside the current data folder.');
  configurationBytes(layout.portableRoot, destination);
  await assertUpdatesSettled(layout.dataDirectory);
  const sourceIdentity = await readDirectoryIdentity(layout.dataDirectory);
  let destinationIdentity: DirectoryIdentity | null = null;
  if (await dataDirectoryExists(destination)) {
    await assertEmptyDirectory(destination);
    destinationIdentity = await readDirectoryIdentity(destination);
  }
  const operationId = randomUUID();
  const stagingDirectory = path.join(
    path.dirname(destination),
    `.${path.basename(destination)}.ahk-manager-move-${operationId}`,
  );
  // Include the largest supported file identities before creating any staging folder.
  const identityBudget = { dev: '0'.repeat(30), ino: '0'.repeat(30), birthtimeNs: '0'.repeat(30) };
  const pendingTemplate: PendingMove = {
    schemaVersion: 1,
    operationId,
    sourceDataDirectory: layout.dataDirectory,
    destinationDataDirectory: destination,
    stagingDirectory,
    createdAtUtc: new Date().toISOString(),
    sourceIdentity,
    stageIdentity: identityBudget,
    destinationIdentity,
  };
  if (Buffer.byteLength(JSON.stringify(pendingTemplate), 'utf8') > 16384)
    throw fail('The data move paths are too long. Choose a shorter path.');
  assertStoragePath(path.dirname(stagingDirectory));
  if (!(await dataDirectoryExists(path.dirname(stagingDirectory))))
    throw fail('Choose a folder whose parent already exists.');
  if (await dataDirectoryExists(stagingDirectory))
    throw fail('The data staging folder already exists.');
  await preparePrivateDirectory(stagingDirectory);
  await assertEmptyDirectory(stagingDirectory);
  const stageIdentity = await readDirectoryIdentity(stagingDirectory);
  const pending: PendingMove = { ...pendingTemplate, stageIdentity };
  try {
    assertStoragePath(layout.portableRoot);
    const temporary = path.join(layout.portableRoot, `.portable-data-move-${operationId}.tmp`);
    await fs.writeFile(temporary, JSON.stringify(pending), {
      flag: 'wx',
      mode: 0o600,
      flush: true,
    });
    const intent = path.join(layout.portableRoot, portableMoveName);
    if (await dataDirectoryExists(intent))
      throw fail('Another data move was prepared before this one completed.');
    // The live backend's per-user effect lease and the main-process request guard
    // serialize publication. An interrupted temporary file never becomes intent.
    await fs.rename(temporary, intent);
  } catch (error) {
    // This is the newly created, still-empty stage; do not remove anything added concurrently.
    if (sameDirectoryIdentity(await readDirectoryIdentity(stagingDirectory), stageIdentity))
      await fs.rmdir(stagingDirectory).catch(() => {});
    throw error;
  }
  return { state: getStorageState(layout), restartRequired: true };
}

async function removePending(layout: DeploymentLayout, pending: PendingMove): Promise<void> {
  requirePortable(layout);
  const current = readPending(layout);
  if (current?.operationId !== pending.operationId) throw fail('The pending data move changed.');
  await fs.unlink(path.join(layout.portableRoot, portableMoveName));
}

/** Cancel only intent. Recovery copies and original data remain available. */
export async function cancelPendingMove(layout: DeploymentLayout): Promise<StorageState> {
  requirePortable(layout);
  const pending = readPending(layout);
  if (pending) await removePending(layout, pending);
  return getStorageState(layout);
}

/** A failed move may reopen only the same existing profile, never a new empty location. */
export async function canKeepCurrentData(layout: DeploymentLayout): Promise<boolean> {
  if (layout.mode !== 'portable') return false;
  try {
    const pending = readPending(layout);
    if (!pending) return false;
    const isSource = samePath(layout.dataDirectory, pending.sourceDataDirectory);
    const isDestination = samePath(layout.dataDirectory, pending.destinationDataDirectory);
    if (!isSource && !isDestination) return false;
    const expected = isSource ? pending.sourceIdentity : pending.stageIdentity;
    if (!sameDirectoryIdentity(await readDirectoryIdentity(layout.dataDirectory), expected))
      return false;
    readStorageJson(path.join(layout.profileDirectory, 'settings.json'), 8 * 1024 * 1024);
    return true;
  } catch {
    return false;
  }
}

/** Runs under the native namespace lease after claiming the original instance lock,
 * before opening the backend profile or a browser window. */
export async function completePendingDataMove(
  layout: DeploymentLayout,
  boundary?: DataMoveBoundary,
): Promise<DeploymentLayout> {
  if (layout.mode !== 'portable') return layout;
  requirePortable(layout);
  const pending = readPending(layout);
  if (!pending) return layout;
  boundary?.check();
  if (samePath(layout.dataDirectory, pending.destinationDataDirectory)) {
    if (
      !sameDirectoryIdentity(
        await readDirectoryIdentity(pending.destinationDataDirectory),
        pending.stageIdentity,
      )
    )
      throw fail(
        'The committed data location changed. Its data and recovery copies were preserved.',
      );
    await removePending(layout, pending);
    return layout;
  }
  if (
    !sameDirectoryIdentity(
      await readDirectoryIdentity(pending.sourceDataDirectory),
      pending.sourceIdentity,
    )
  )
    throw fail('The original data folder changed after the move was prepared.');
  await assertUpdatesSettled(pending.sourceDataDirectory);
  const original = await inventoryData(pending.sourceDataDirectory);
  boundary?.check();
  let promoted = false;
  if (await dataDirectoryExists(pending.stagingDirectory)) {
    if (
      !sameDirectoryIdentity(
        await readDirectoryIdentity(pending.stagingDirectory),
        pending.stageIdentity,
      )
    )
      throw fail('The data staging folder changed after preparation.');
    await copyDataInventory(
      pending.sourceDataDirectory,
      pending.stagingDirectory,
      original,
      boundary?.check,
    );
  } else if (
    (await dataDirectoryExists(pending.destinationDataDirectory)) &&
    sameDirectoryIdentity(
      await readDirectoryIdentity(pending.destinationDataDirectory),
      pending.stageIdentity,
    )
  ) {
    if (
      fingerprintData(await inventoryData(pending.destinationDataDirectory, true)) !==
      fingerprintData(original)
    )
      throw fail('The promoted data copy no longer matches the original.');
    promoted = true;
  } else throw fail('The prepared data copy is unavailable. The original folder was preserved.');
  if (
    fingerprintData(await inventoryData(pending.sourceDataDirectory)) !== fingerprintData(original)
  )
    throw fail('The source data changed while it was copied. The original folder was preserved.');
  if (!promoted) {
    boundary?.check();
    if (await dataDirectoryExists(pending.destinationDataDirectory)) {
      if (
        !pending.destinationIdentity ||
        !sameDirectoryIdentity(
          await readDirectoryIdentity(pending.destinationDataDirectory),
          pending.destinationIdentity,
        )
      )
        throw fail('Another folder appeared at the data destination. It was preserved.');
      await assertEmptyDirectory(pending.destinationDataDirectory);
      await fs.rmdir(pending.destinationDataDirectory);
    }
    assertStoragePath(path.dirname(pending.destinationDataDirectory));
    await boundary?.beforePromotion();
    boundary?.check();
    // Both directories are on the destination volume. Windows refuses a concurrently
    // created destination directory; no copy uses an overwrite or merge operation.
    await fs.rename(pending.stagingDirectory, pending.destinationDataDirectory);
  }
  boundary?.check();
  if (
    !sameDirectoryIdentity(
      await readDirectoryIdentity(pending.destinationDataDirectory),
      pending.stageIdentity,
    ) ||
    fingerprintData(await inventoryData(pending.destinationDataDirectory, true)) !==
      fingerprintData(original)
  )
    throw fail('The final data copy could not be verified. The original folder was preserved.');
  const configured = parsePortableConfiguration(readStorageJson(layout.configurationPath, 4096));
  if (
    !samePath(
      validateDataDirectory(layout.portableRoot, configured.dataDirectory),
      pending.sourceDataDirectory,
    )
  )
    throw fail('Portable settings changed during the data move.');
  const temporary = path.join(
    layout.portableRoot,
    `.portable-data-config-${pending.operationId}.tmp`,
  );
  const bytes = configurationBytes(layout.portableRoot, pending.destinationDataDirectory);
  boundary?.check();
  if (await dataDirectoryExists(temporary)) {
    const existing = parsePortableConfiguration(readStorageJson(temporary, 4096));
    if (JSON.stringify(existing) !== bytes)
      throw fail('A different portable settings file occupies the migration staging path.');
  } else await fs.writeFile(temporary, bytes, { flag: 'wx', mode: 0o600, flush: true });
  boundary?.check();
  await fs.rename(temporary, layout.configurationPath);
  const result: DeploymentLayout = {
    ...layout,
    dataDirectory: pending.destinationDataDirectory,
    profileDirectory: path.join(pending.destinationDataDirectory, 'Profile'),
    chromiumDirectory: path.join(pending.destinationDataDirectory, 'Chromium'),
    updatesDirectory: path.join(pending.destinationDataDirectory, 'Updates'),
  };
  await removePending(result, pending);
  return result;
}

/** Preserve only the public channel preference; Setup packages are not portable rollback packages. */
export async function importLegacyUpdateChannel(layout: DeploymentLayout): Promise<void> {
  if (layout.mode !== 'portable' || samePath(layout.dataDirectory, layout.legacyDataDirectory))
    return;
  await readDirectoryIdentity(layout.dataDirectory);
  const target = path.join(layout.updatesDirectory, 'update-channel.json');
  if (await dataDirectoryExists(target)) return;
  const source = path.join(layout.legacyDataDirectory, 'Updates', 'update-channel.json');
  let channel: unknown;
  try {
    channel = readStorageJson(source, 1024);
  } catch (error) {
    if (missing(error)) return;
    throw fail('The existing update channel could not be imported.');
  }
  if (
    !storageObject(channel) ||
    !storageKeys(channel, ['schemaVersion', 'channel']) ||
    channel.schemaVersion !== 1 ||
    !['stable', 'test'].includes(String(channel.channel))
  )
    throw fail(
      'The existing update channel is invalid. Select an update channel in the portable app.',
    );
  assertStoragePath(layout.updatesDirectory);
  if (!(await dataDirectoryExists(layout.updatesDirectory)))
    await fs.mkdir(layout.updatesDirectory, { mode: 0o700 });
  await readDirectoryIdentity(layout.updatesDirectory);
  try {
    await fs.writeFile(target, JSON.stringify(channel), { flag: 'wx', mode: 0o600 });
  } catch (error) {
    if ((error as NodeJS.ErrnoException)?.code !== 'EEXIST') throw error;
  }
}
