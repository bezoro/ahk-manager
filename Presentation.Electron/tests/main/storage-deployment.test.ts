import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { resolveDeployment, validateDataDirectory } from '../../src/main/storage/deployment.js';

async function fixture(t: TestContext) {
  const temporary = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(temporary && path.isAbsolute(temporary));
  const parent = await fs.mkdtemp(path.join(temporary, 'storage-layout-'));
  t.after(() => fs.rm(parent, { recursive: true, force: true }));
  const root = path.join(parent, 'Portable manager');
  const executable = path.join(root, 'App', 'Bezoro.AhkManager.exe');
  const local = path.join(parent, 'Local');
  await fs.mkdir(path.dirname(executable), { recursive: true });
  await fs.mkdir(local);
  const marker = path.join(root, 'portable.json');
  const write = (dataDirectory = 'Data') =>
    fs.writeFile(
      marker,
      JSON.stringify({
        schemaVersion: 1,
        applicationId: 'Bezoro.AhkManager.Electron',
        dataDirectory,
      }),
    );
  return { parent, root, executable, local, marker, write };
}

test('installed layouts retain the existing LocalAppData profile and updater paths', async (t) => {
  const { executable, local } = await fixture(t);
  const layout = resolveDeployment(executable, local);
  const existing = path.join(local, 'Bezoro', 'AhkManagerElectron');
  assert.equal(layout.mode, 'installed');
  assert.equal(layout.profileDirectory, existing);
  assert.equal(layout.dataDirectory, existing);
  assert.equal(layout.updatesDirectory, path.join(existing, 'Updates'));
  assert.equal(layout.chromiumDirectory, null);
  assert.equal(layout.launchExecutable, executable);
});

test('portable defaults keep mutable Data separate from the replaceable App payload', async (t) => {
  const { root, executable, local, write } = await fixture(t);
  await write();
  const layout = resolveDeployment(executable, local);
  assert.equal(layout.mode, 'portable');
  assert.equal(layout.dataDirectory, path.join(root, 'Data'));
  assert.equal(layout.profileDirectory, path.join(root, 'Data', 'Profile'));
  assert.equal(layout.chromiumDirectory, path.join(root, 'Data', 'Chromium'));
  assert.equal(layout.updatesDirectory, path.join(root, 'Data', 'Updates'));
  assert.equal(layout.launchExecutable, path.join(root, 'Bezoro.AhkManager.exe'));
});

test('a normalized custom local data folder resolves through the same strict marker', async (t) => {
  const { parent, root, executable, local, write } = await fixture(t);
  const selected = path.join(parent, 'Custom data with espaço');
  await write(selected);
  assert.equal(resolveDeployment(executable, local).dataDirectory, selected);
  await write(path.join('Other', 'Data'));
  assert.equal(
    resolveDeployment(executable, local).dataDirectory,
    path.join(root, 'Other', 'Data'),
  );
});

test('a packaged portable payload with missing settings cannot silently become an installed profile', async (t) => {
  const { root, executable, local } = await fixture(t);
  await fs.writeFile(path.join(root, 'portable-package.json'), '{}');
  assert.throws(() => resolveDeployment(executable, local), /settings file is missing/);
});

test('a selected data location cannot be a regular file or an alternate stream', async (t) => {
  const { root, parent } = await fixture(t);
  const file = path.join(parent, 'occupied-file');
  await fs.writeFile(file, 'preserved');
  assert.throws(() => validateDataDirectory(root, file), /refers to a file/);
  assert.throws(() => validateDataDirectory(root, path.join(parent, 'Data:stream')), /stream/);
  assert.equal(await fs.readFile(file, 'utf8'), 'preserved');
});

test('malformed, duplicate, unknown and mismatched portable markers never fall back to an empty installed profile', async (t) => {
  const { executable, local, marker } = await fixture(t);
  for (const text of [
    '{',
    '[]',
    '{}',
    '{"schemaVersion":1,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"Data","extra":true}',
    '{"schemaVersion":2,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"Data"}',
    '{"schemaVersion":1,"applicationId":"Other.App","dataDirectory":"Data"}',
    '{"schemaVersion":1,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"First","dataDirectory":"Data"}',
    '{"schemaVersion":1,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"First","data\\u0044irectory":"Data"}',
  ]) {
    await fs.writeFile(marker, text);
    assert.throws(() => resolveDeployment(executable, local));
  }
  await fs.writeFile(marker, Buffer.from([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]));
  assert.throws(() => resolveDeployment(executable, local));
});

test('data path validation rejects payload overlap, root replacement and ambiguous names', async (t) => {
  const { root } = await fixture(t);
  for (const value of [
    '',
    '.',
    '..',
    path.join(root, 'App'),
    path.join(root, 'App', 'Data'),
    root,
    path.dirname(root),
    path.parse(root).root,
    'portable.json',
    path.join('portable-package.json', 'Data'),
    'Bezoro.AhkManager.exe',
    '.app-stage-example',
    '.app-previous-example',
    '.portable-data-config-example.tmp',
    path.join('.portable-data-move-example.tmp', 'Data'),
    'CON',
    'Data.',
    'Data ',
    `Data${path.sep}..${path.sep}Other`,
    '\\\\server\\share',
  ]) {
    assert.throws(() => validateDataDirectory(root, value), value);
  }
});

test('redirected marker files and linked marker identities are refused', async (t) => {
  const { parent, executable, local, marker, write } = await fixture(t);
  await write();
  const other = path.join(parent, 'marker-copy.json');
  await fs.link(marker, other);
  assert.throws(() => resolveDeployment(executable, local), /regular/);
  await fs.unlink(other);
  await fs.rename(marker, other);
  try {
    await fs.symlink(other, marker, 'file');
  } catch (error) {
    if (['EPERM', 'EACCES', 'ENOTSUP'].includes((error as NodeJS.ErrnoException).code ?? '')) {
      t.skip('The runner cannot create this owned symlink fixture.');
      return;
    }
    throw error;
  }
  assert.throws(() => resolveDeployment(executable, local), /redirected/);
});
