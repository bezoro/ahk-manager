import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { resolveDeployment } from '../../src/main/storage/deployment.js';
import {
  canKeepCurrentData,
  cancelPendingMove,
  completePendingDataMove,
  getStorageState,
  importLegacyUpdateChannel,
  prepareDataMove,
} from '../../src/main/storage/data-migration.js';

async function fixture(t: TestContext) {
  const temporary = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(temporary && path.isAbsolute(temporary));
  const parent = await fs.mkdtemp(path.join(temporary, 'storage-move-'));
  t.after(() => fs.rm(parent, { recursive: true, force: true }));
  const root = path.join(parent, 'Portable');
  const executable = path.join(root, 'App', 'Bezoro.AhkManager.exe');
  const local = path.join(parent, 'Local');
  await fs.mkdir(path.dirname(executable), { recursive: true });
  await fs.mkdir(local);
  const marker = path.join(root, 'portable.json');
  const initial = JSON.stringify({
    schemaVersion: 1,
    applicationId: 'Bezoro.AhkManager.Electron',
    dataDirectory: 'Data',
  });
  await fs.writeFile(marker, initial);
  const layout = resolveDeployment(executable, local);
  await fs.mkdir(layout.profileDirectory, { recursive: true });
  await fs.mkdir(layout.updatesDirectory);
  await fs.writeFile(path.join(layout.profileDirectory, 'settings.json'), '{"fixture":"before"}');
  await fs.writeFile(
    path.join(layout.profileDirectory, 'activity.jsonl'),
    '{"fixture":"activity"}\n',
  );
  await fs.writeFile(
    path.join(layout.updatesDirectory, 'update-channel.json'),
    '{"schemaVersion":1,"channel":"test"}',
  );
  const target = path.join(parent, 'Selected data');
  const preparePrivate = async (directory: string): Promise<void> => {
    await fs.mkdir(directory, { mode: 0o700 });
  };
  const pendingPath = path.join(root, 'portable-data-move.json');
  const pending = async () =>
    JSON.parse(await fs.readFile(pendingPath, 'utf8')) as {
      stagingDirectory: string;
      operationId: string;
    };
  return {
    parent,
    root,
    executable,
    local,
    marker,
    initial,
    layout,
    target,
    preparePrivate,
    pendingPath,
    pending,
  };
}

test('changing data location prepares intent and transfers the final closed-session snapshot on restart', async (t) => {
  const f = await fixture(t);
  await fs.mkdir(path.join(f.layout.dataDirectory, 'Chromium'));
  await fs.writeFile(
    path.join(f.layout.dataDirectory, 'Chromium', 'runtime-cache'),
    'not migrated',
  );
  await fs.mkdir(path.join(f.layout.dataDirectory, 'My files'));
  await fs.writeFile(
    path.join(f.layout.dataDirectory, 'My files', 'user.txt'),
    'preserved user data',
  );
  const result = await prepareDataMove(f.layout, f.target, f.preparePrivate);
  assert.equal(result.restartRequired, true);
  assert.equal(result.state.pendingDataDirectory, f.target);
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  // Saving window placement and other normal operations after preparation is allowed.
  const finalSettings = '{"fixture":"written before normal restart"}';
  await fs.writeFile(path.join(f.layout.profileDirectory, 'settings.json'), finalSettings);
  const relocated = await completePendingDataMove(f.layout);
  assert.equal(relocated.dataDirectory, f.target);
  assert.equal(
    await fs.readFile(path.join(relocated.profileDirectory, 'settings.json'), 'utf8'),
    finalSettings,
  );
  assert.equal(
    await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
    finalSettings,
  );
  assert.equal(
    await fs.readFile(path.join(f.target, 'My files', 'user.txt'), 'utf8'),
    'preserved user data',
  );
  await assert.rejects(fs.stat(path.join(f.target, 'Chromium')), { code: 'ENOENT' });
  assert.deepEqual(
    JSON.parse(
      await fs.readFile(path.join(relocated.updatesDirectory, 'update-channel.json'), 'utf8'),
    ),
    { schemaVersion: 1, channel: 'test' },
  );
  assert.equal(resolveDeployment(f.executable, f.local).dataDirectory, f.target);
  assert.equal(getStorageState(relocated).pendingDataDirectory, null);
});

test('existing nonempty destinations are never merged or overwritten', async (t) => {
  const f = await fixture(t);
  await fs.mkdir(f.target);
  await fs.writeFile(path.join(f.target, 'foreign.txt'), 'foreign');
  let requested = false;
  await assert.rejects(
    prepareDataMove(f.layout, f.target, async () => {
      requested = true;
    }),
    /empty/,
  );
  assert.equal(requested, false);
  assert.equal(await fs.readFile(path.join(f.target, 'foreign.txt'), 'utf8'), 'foreign');
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
});

test('a Unicode destination exceeding the marker byte limit is refused before staging', async (t) => {
  const f = await fixture(t);
  let target = f.parent;
  const segment = '界'.repeat(40);
  while (Buffer.byteLength(path.join(target, segment), 'utf8') < 4000)
    target = path.join(target, segment);
  target = path.join(target, 'x'.repeat(4050 - Buffer.byteLength(target, 'utf8') - 1));
  assert.equal(Buffer.byteLength(target, 'utf8'), 4050);
  let prepared = false;
  await assert.rejects(
    prepareDataMove(f.layout, target, async () => {
      prepared = true;
    }),
    /path is too long/,
  );
  assert.equal(prepared, false);
  await assert.rejects(fs.stat(f.pendingPath), { code: 'ENOENT' });
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
});

test('an explicitly selected empty destination is replaced only after its identity is rechecked', async (t) => {
  const f = await fixture(t);
  await fs.mkdir(f.target);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  await completePendingDataMove(f.layout);
  assert.equal(
    await fs.readFile(path.join(f.target, 'Profile', 'settings.json'), 'utf8'),
    '{"fixture":"before"}',
  );
});

test('a folder appearing after preparation is preserved and the unchanged source remains usable', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  await fs.mkdir(f.target);
  await fs.writeFile(path.join(f.target, 'foreign.txt'), 'concurrent owner');
  await assert.rejects(completePendingDataMove(f.layout), /Another folder/);
  assert.equal(await canKeepCurrentData(f.layout), true);
  assert.equal(await fs.readFile(path.join(f.target, 'foreign.txt'), 'utf8'), 'concurrent owner');
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  const { stagingDirectory } = await f.pending();
  await cancelPendingMove(f.layout);
  assert.equal(
    await fs.readFile(path.join(stagingDirectory, 'Profile', 'settings.json'), 'utf8'),
    '{"fixture":"before"}',
  );
  assert.equal(getStorageState(f.layout).pendingDataDirectory, null);
});

test('source mutations during copying prevent pointer commit and preserve both snapshots', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  const original = fs.copyFile.bind(fs);
  let altered = false;
  t.mock.method(fs, 'copyFile', async (...args: Parameters<typeof fs.copyFile>) => {
    await original(...args);
    if (!altered && path.basename(String(args[0])) === 'settings.json') {
      altered = true;
      await fs.writeFile(
        path.join(f.layout.profileDirectory, 'settings.json'),
        '{"fixture":"concurrent change"}',
      );
    }
  });
  await assert.rejects(completePendingDataMove(f.layout), /source data changed/);
  assert.equal(await canKeepCurrentData(f.layout), true);
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  assert.equal(
    await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
    '{"fixture":"concurrent change"}',
  );
  const { stagingDirectory } = await f.pending();
  assert.equal(
    await fs.readFile(path.join(stagingDirectory, 'Profile', 'settings.json'), 'utf8'),
    '{"fixture":"before"}',
  );
});

test('a replaced source directory never becomes a blank-profile fallback', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  await fs.rename(f.layout.dataDirectory, f.layout.dataDirectory + '.preserved');
  await fs.mkdir(f.layout.dataDirectory);
  await assert.rejects(completePendingDataMove(f.layout), /original data folder changed/);
  assert.equal(await canKeepCurrentData(f.layout), false);
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
});

test('a pointer changed during relocation cannot authorize an unrelated fallback profile', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  const unrelated = path.join(f.parent, 'Unrelated data');
  await fs.mkdir(path.join(unrelated, 'Profile'), { recursive: true });
  await fs.writeFile(path.join(unrelated, 'Profile', 'settings.json'), '{"fixture":"unrelated"}');
  await fs.writeFile(
    f.marker,
    JSON.stringify({
      schemaVersion: 1,
      applicationId: 'Bezoro.AhkManager.Electron',
      dataDirectory: unrelated,
    }),
  );
  await assert.rejects(completePendingDataMove(f.layout), /Portable settings changed/);
  assert.equal(await canKeepCurrentData(f.layout), true);
  const reResolved = resolveDeployment(f.executable, f.local);
  assert.equal(reResolved.dataDirectory, unrelated);
  assert.equal(await canKeepCurrentData(reResolved), false);
  assert.equal(
    await fs.readFile(path.join(unrelated, 'Profile', 'settings.json'), 'utf8'),
    '{"fixture":"unrelated"}',
  );
  assert.equal(
    await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
    '{"fixture":"before"}',
  );
});

test('interrupted directory promotion resumes by the original stage identity', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  const { stagingDirectory } = await f.pending();
  await fs.cp(f.layout.profileDirectory, path.join(stagingDirectory, 'Profile'), {
    recursive: true,
  });
  await fs.cp(f.layout.updatesDirectory, path.join(stagingDirectory, 'Updates'), {
    recursive: true,
  });
  await fs.rename(stagingDirectory, f.target);
  await completePendingDataMove(f.layout);
  assert.equal(resolveDeployment(f.executable, f.local).dataDirectory, f.target);
  assert.equal(
    getStorageState(resolveDeployment(f.executable, f.local)).pendingDataDirectory,
    null,
  );
});

test('a committed pointer with a leftover intent never recopies older source files', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  const intent = await fs.readFile(f.pendingPath);
  const completed = await completePendingDataMove(f.layout);
  await fs.writeFile(f.pendingPath, intent);
  await fs.writeFile(
    path.join(completed.profileDirectory, 'settings.json'),
    '{"fixture":"new session"}',
  );
  await completePendingDataMove(completed);
  assert.equal(
    await fs.readFile(path.join(completed.profileDirectory, 'settings.json'), 'utf8'),
    '{"fixture":"new session"}',
  );
  assert.equal(getStorageState(completed).pendingDataDirectory, null);
});

test('hidden runtime entries injected into a partial stage cannot bypass verification', async (t) => {
  const f = await fixture(t);
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  const { stagingDirectory } = await f.pending();
  await fs.mkdir(path.join(stagingDirectory, 'Chromium'));
  await fs.writeFile(path.join(stagingDirectory, 'Chromium', 'foreign'), 'foreign');
  await assert.rejects(completePendingDataMove(f.layout), /staged data changed/);
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
});

test('unknown recovery outcomes block data relocation while a settled refusal does not', async (t) => {
  const f = await fixture(t);
  const operationId = randomUUID();
  const operation = path.join(f.layout.updatesDirectory, 'operations', operationId);
  await fs.mkdir(operation, { recursive: true });
  await fs.writeFile(
    path.join(operation, 'outcome.json'),
    JSON.stringify({ schemaVersion: 1, operationId, outcome: 'Unknown' }),
  );
  await assert.rejects(prepareDataMove(f.layout, f.target, f.preparePrivate), /unfinished update/);
  await fs.writeFile(
    path.join(operation, 'outcome.json'),
    JSON.stringify({ schemaVersion: 1, operationId, outcome: 'Failed' }),
  );
  assert.equal((await prepareDataMove(f.layout, f.target, f.preparePrivate)).restartRequired, true);
});

test('linked operational files are refused without modifying the original profile', async (t) => {
  const f = await fixture(t);
  const first = path.join(f.layout.dataDirectory, 'linked-file');
  await fs.writeFile(first, 'linked');
  await fs.link(first, path.join(f.layout.dataDirectory, 'second-link'));
  await prepareDataMove(f.layout, f.target, f.preparePrivate);
  await assert.rejects(completePendingDataMove(f.layout), /unsupported linked/);
  assert.equal(await canKeepCurrentData(f.layout), true);
  assert.equal(await fs.readFile(first, 'utf8'), 'linked');
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
});

for (const history of [false, true])
  test(`cloud-tagged ${history ? 'update history' : 'profile files'} remain transferable with verified bytes`, async (t) => {
    const enumerate = fs.readdir;
    // libuv on Windows reports cloud reparse points as link Dirents, while lstat
    // correctly reports their underlying regular file or directory. Model both
    // APIs over real fixture data so a move still has to copy and verify bytes.
    t.mock.method(fs, 'readdir', (async (...args: Parameters<typeof fs.readdir>) => {
      const entries: unknown = await Reflect.apply(enumerate, fs, args);
      if (
        args[1] &&
        typeof args[1] === 'object' &&
        'withFileTypes' in args[1] &&
        args[1].withFileTypes &&
        Array.isArray(entries)
      )
        return entries.map(
          (entry) =>
            new Proxy(entry, {
              get(target, key, receiver) {
                if (key === 'isSymbolicLink') return () => true;
                if (key === 'isDirectory' || key === 'isFile') return () => false;
                return Reflect.get(target, key, receiver);
              },
            }),
        );
      return entries;
    }) as typeof fs.readdir);
    const f = await fixture(t);
    let operation: string | null = null;
    if (history) {
      const operationId = randomUUID();
      operation = path.join('Updates', 'operations', operationId, 'outcome.json');
      const file = path.join(f.layout.dataDirectory, operation);
      await fs.mkdir(path.dirname(file), { recursive: true });
      await fs.writeFile(
        file,
        JSON.stringify({ schemaVersion: 1, operationId, outcome: 'Failed' }),
      );
    }
    await prepareDataMove(f.layout, f.target, f.preparePrivate);
    const moved = await completePendingDataMove(f.layout);
    assert.equal(
      await fs.readFile(path.join(moved.profileDirectory, 'settings.json'), 'utf8'),
      '{"fixture":"before"}',
    );
    assert.equal(
      await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
      '{"fixture":"before"}',
    );
    if (operation)
      assert.deepEqual(
        await fs.readFile(path.join(moved.dataDirectory, operation)),
        await fs.readFile(path.join(f.layout.dataDirectory, operation)),
      );
    assert.equal(resolveDeployment(f.executable, f.local).dataDirectory, f.target);
  });

test('actual linked data and update directories remain refused without touching their targets', async (t) => {
  for (const history of [false, true]) {
    const f = await fixture(t);
    const target = path.join(f.parent, 'External linked folder');
    await fs.mkdir(target);
    const preserved = path.join(target, 'preserved.txt');
    await fs.writeFile(preserved, 'External content must remain unchanged.');
    const link = history
      ? path.join(f.layout.updatesDirectory, 'operations', randomUUID())
      : path.join(f.layout.dataDirectory, 'Linked data');
    await fs.mkdir(path.dirname(link), { recursive: true });
    await fs.symlink(target, link, process.platform === 'win32' ? 'junction' : 'dir');
    if (history)
      await assert.rejects(
        prepareDataMove(f.layout, f.target, f.preparePrivate),
        /recovery history could not be verified/,
      );
    else {
      await prepareDataMove(f.layout, f.target, f.preparePrivate);
      await assert.rejects(completePendingDataMove(f.layout), /redirected entry/);
    }
    assert.equal(await fs.readFile(preserved, 'utf8'), 'External content must remain unchanged.');
    assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  }
});

test('the legacy public update-channel preference imports once without Setup packages or credentials', async (t) => {
  const f = await fixture(t);
  const old = path.join(f.layout.legacyDataDirectory, 'Updates');
  await fs.mkdir(old, { recursive: true });
  await fs.writeFile(
    path.join(old, 'update-channel.json'),
    '{"schemaVersion":1,"channel":"stable"}',
  );
  await fs.writeFile(path.join(old, 'github-token.bin'), 'never read');
  await fs.unlink(path.join(f.layout.updatesDirectory, 'update-channel.json'));
  await importLegacyUpdateChannel(f.layout);
  assert.deepEqual(
    JSON.parse(
      await fs.readFile(path.join(f.layout.updatesDirectory, 'update-channel.json'), 'utf8'),
    ),
    { schemaVersion: 1, channel: 'stable' },
  );
  await fs.writeFile(path.join(old, 'update-channel.json'), '{"schemaVersion":1,"channel":"test"}');
  await importLegacyUpdateChannel(f.layout);
  assert.equal(
    JSON.parse(
      await fs.readFile(path.join(f.layout.updatesDirectory, 'update-channel.json'), 'utf8'),
    ).channel,
    'stable',
  );
  await assert.rejects(fs.stat(path.join(f.layout.updatesDirectory, 'github-token.bin')), {
    code: 'ENOENT',
  });
  assert.equal(await fs.readFile(path.join(old, 'github-token.bin'), 'utf8'), 'never read');
});

test('invalid legacy channel data is preserved and never committed into portable history', async (t) => {
  const f = await fixture(t);
  const old = path.join(f.layout.legacyDataDirectory, 'Updates');
  await fs.mkdir(old, { recursive: true });
  await fs.writeFile(
    path.join(old, 'update-channel.json'),
    '{"schemaVersion":1,"channel":"stable","channel":"test"}',
  );
  await fs.unlink(path.join(f.layout.updatesDirectory, 'update-channel.json'));
  await assert.rejects(importLegacyUpdateChannel(f.layout), /could not be imported/);
  await assert.rejects(fs.stat(path.join(f.layout.updatesDirectory, 'update-channel.json')), {
    code: 'ENOENT',
  });
});
