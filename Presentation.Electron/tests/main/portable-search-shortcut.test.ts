import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import sync from 'node:fs';
import path from 'node:path';
import type { ShortcutDetails } from 'electron';
import {
  ensurePortableSearchShortcut,
  type PortableShortcutBoundary,
} from '../../src/main/desktop/portable-search-shortcut.js';
import { resolveDeployment } from '../../src/main/storage/deployment.js';

async function fixture(t: TestContext) {
  const parent = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(parent && path.isAbsolute(parent));
  const temporary = await fs.mkdtemp(path.join(parent, 'portable-shortcut-'));
  t.after(() => fs.rm(temporary, { recursive: true, force: true }));
  const root = path.join(temporary, 'Portable manager');
  const actualHost = path.join(root, 'App', 'Bezoro.AhkManager.exe');
  await fs.mkdir(path.dirname(actualHost), { recursive: true });
  await fs.writeFile(actualHost, 'Inert app fixture. Never executed.');
  await fs.writeFile(path.join(root, 'Bezoro.AhkManager.exe'), 'Inert launcher. Never executed.');
  await fs.writeFile(
    path.join(root, 'portable.json'),
    JSON.stringify({
      schemaVersion: 1,
      applicationId: 'Bezoro.AhkManager.Electron',
      dataDirectory: 'Data',
    }),
  );
  const appData = path.join(temporary, 'Roaming');
  const local = path.join(temporary, 'Local');
  await fs.mkdir(appData);
  await fs.mkdir(local);
  const layout = resolveDeployment(actualHost, local);
  const programs = path.join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs');
  const shortcut = path.join(programs, 'AHK Manager Portable.lnk');
  const writes: Array<{ file: string; details: ShortcutDetails }> = [];
  let permitted = true;
  // JSON files model the shell adapter. All namespace publication, identity
  // checks, relocation, hardlinks and cleanup use the real fixture filesystem.
  const boundary: PortableShortcutBoundary = {
    platform: 'win32',
    appDataDirectory: appData,
    permitted: () => permitted,
    read: (file) => JSON.parse(sync.readFileSync(file, 'utf8')) as ShortcutDetails,
    write: (file, operation, details) => {
      assert.equal(operation, 'create');
      assert.notEqual(file, shortcut, 'the shell must only write a freshly claimed stage');
      writes.push({ file, details });
      sync.writeFileSync(file, JSON.stringify(details));
      return true;
    },
  };
  return {
    temporary,
    root,
    local,
    layout,
    programs,
    shortcut,
    writes,
    boundary,
    setPermitted: (value: boolean) => {
      permitted = value;
    },
    move: async () => {
      const relocated = path.join(temporary, 'Moved portable manager');
      await fs.rename(root, relocated);
      return resolveDeployment(path.join(relocated, 'App', 'Bezoro.AhkManager.exe'), local);
    },
  };
}

test('portable startup creates a current-user application shortcut with root launcher metadata and remains idempotent', async (t) => {
  const f = await fixture(t);
  assert.equal(await ensurePortableSearchShortcut(f.layout, f.boundary), 'created');
  assert.deepEqual(f.boundary.read(f.shortcut), {
    target: path.join(f.root, 'Bezoro.AhkManager.exe'),
    cwd: f.root,
    args: '--foreground',
    description: 'Manage AutoHotkey scripts with AHK Manager Portable.',
    icon: path.join(f.root, 'Bezoro.AhkManager.exe'),
    iconIndex: 0,
    appUserModelId: 'Bezoro.AhkManager.Electron',
  });
  assert.deepEqual(await fs.readdir(f.programs), ['AHK Manager Portable.lnk']);
  const before = await fs.lstat(f.shortcut, { bigint: true });
  assert.equal(before.nlink, 1n);
  assert.equal(await ensurePortableSearchShortcut(f.layout, f.boundary), 'unchanged');
  const after = await fs.lstat(f.shortcut, { bigint: true });
  assert.equal(after.ino, before.ino);
  assert.equal(after.mtimeNs, before.mtimeNs);
  assert.equal(f.writes.length, 1);
});

test('moving portable Manager refreshes its owned target, working directory and icon without changing profile or script references', async (t) => {
  const f = await fixture(t);
  const script = path.join(f.temporary, 'external-script.ahk');
  await fs.writeFile(script, '; external script remains here');
  await fs.mkdir(f.layout.profileDirectory, { recursive: true });
  const profile = JSON.stringify({ scripts: [{ path: script }] });
  await fs.writeFile(path.join(f.layout.profileDirectory, 'settings.json'), profile);
  await ensurePortableSearchShortcut(f.layout, f.boundary);
  const relocated = await f.move();
  assert.equal(await ensurePortableSearchShortcut(relocated, f.boundary), 'updated');
  const link = f.boundary.read(f.shortcut);
  assert.equal(link.target, relocated.launchExecutable);
  assert.equal(link.cwd, relocated.portableRoot);
  assert.equal(link.icon, relocated.launchExecutable);
  assert.equal(link.args, '--foreground');
  assert.deepEqual(await fs.readdir(f.programs), ['AHK Manager Portable.lnk']);
  assert.equal(
    await fs.readFile(path.join(relocated.profileDirectory, 'settings.json'), 'utf8'),
    profile,
  );
  assert.equal(await fs.readFile(script, 'utf8'), '; external script remains here');
});

test('foreign shortcut ownership or changed launch metadata is preserved byte-for-byte', async (t) => {
  for (const change of [
    { appUserModelId: 'Another.Application' },
    { description: 'User-created shortcut' },
    { args: '--login' },
    { cwd: '' },
    { icon: '' },
    { iconIndex: 1 },
    { target: path.resolve('Another.exe') },
  ]) {
    const f = await fixture(t);
    await ensurePortableSearchShortcut(f.layout, f.boundary);
    const foreign = JSON.stringify({ ...f.boundary.read(f.shortcut), ...change });
    await fs.writeFile(f.shortcut, foreign);
    const writes = f.writes.length;
    assert.equal(await ensurePortableSearchShortcut(f.layout, f.boundary), 'preserved');
    assert.equal(await fs.readFile(f.shortcut, 'utf8'), foreign);
    assert.equal(f.writes.length, writes);
  }
});

test('linked or unreadable shortcut entries and redirected Start Menu folders never receive writes', async (t) => {
  for (const kind of ['symlink', 'hardlink', 'invalid', 'parent'] as const) {
    const f = await fixture(t);
    const external = path.join(f.temporary, 'External');
    await fs.mkdir(external);
    const target = path.join(external, 'foreign-shortcut');
    const contents = 'Foreign content must remain unchanged.';
    await fs.writeFile(target, contents);
    if (kind === 'parent') {
      await fs.mkdir(path.dirname(f.programs), { recursive: true });
      await fs.symlink(external, f.programs, process.platform === 'win32' ? 'junction' : 'dir');
    } else {
      await fs.mkdir(f.programs, { recursive: true });
      if (kind === 'symlink') {
        // Directory junctions require no symlink privilege on Windows.
        await fs.symlink(external, f.shortcut, process.platform === 'win32' ? 'junction' : 'dir');
      } else if (kind === 'hardlink') await fs.link(target, f.shortcut);
      else await fs.writeFile(f.shortcut, contents);
    }
    const result = await ensurePortableSearchShortcut(f.layout, f.boundary);
    assert.equal(result, kind === 'parent' ? 'failed' : 'preserved');
    assert.equal(f.writes.length, 0);
    assert.equal(await fs.readFile(target, 'utf8'), contents);
    assert.deepEqual(await fs.readdir(external), ['foreign-shortcut']);
  }
});

test('installed, non-Windows and unowned sessions do not register portable shortcuts', async (t) => {
  const f = await fixture(t);
  for (const [layout, boundary] of [
    [{ ...f.layout, mode: 'installed' as const }, f.boundary],
    [f.layout, { ...f.boundary, platform: 'linux' as const }],
    [f.layout, { ...f.boundary, permitted: () => false }],
  ] as const)
    assert.equal(await ensurePortableSearchShortcut(layout, boundary), 'not-applicable');
  assert.equal(f.writes.length, 0);
  await assert.rejects(fs.access(f.programs), { code: 'ENOENT' });
});

test('failed shell writes and lost authorization resolve without blocking startup or publishing a shortcut', async (t) => {
  for (const kind of [
    'false',
    'throw',
    'write-then-false',
    'write-then-throw',
    'lost-ownership',
  ] as const) {
    const f = await fixture(t);
    const write = f.boundary.write;
    f.boundary.write = (...args) => {
      if (kind === 'throw') throw new Error('Fixture shell unavailable.');
      if (kind === 'false') return false;
      const result = write(...args);
      if (kind === 'write-then-false') return false;
      if (kind === 'write-then-throw') throw new Error('Fixture shell failed after saving.');
      f.setPermitted(false);
      return result;
    };
    assert.equal(
      await ensurePortableSearchShortcut(f.layout, f.boundary),
      kind === 'lost-ownership' ? 'not-applicable' : 'failed',
    );
    await assert.rejects(fs.access(f.shortcut), { code: 'ENOENT' });
    assert.deepEqual(await fs.readdir(f.programs), []);
  }
});

test('a failed shell call preserves unrecognized partial stage bytes instead of deleting them', async (t) => {
  for (const throws of [false, true]) {
    const f = await fixture(t);
    const partial = 'Unrecognized partial shortcut bytes.';
    f.boundary.write = (file) => {
      sync.writeFileSync(file, partial);
      if (throws) throw new Error('Fixture shell failed after a partial write.');
      return false;
    };
    assert.equal(await ensurePortableSearchShortcut(f.layout, f.boundary), 'failed');
    await assert.rejects(fs.access(f.shortcut), { code: 'ENOENT' });
    const files = await fs.readdir(f.programs);
    assert.equal(files.length, 1);
    assert.match(files[0]!, /^\.ahk-manager-[a-f0-9-]+\.lnk$/);
    assert.equal(await fs.readFile(path.join(f.programs, files[0]!), 'utf8'), partial);
  }
});

test('a foreign shortcut appearing during creation is never replaced', async (t) => {
  const f = await fixture(t);
  const write = f.boundary.write;
  f.boundary.write = (...args) => {
    const result = write(...args);
    sync.writeFileSync(f.shortcut, 'Concurrent foreign shortcut');
    return result;
  };
  assert.equal(await ensurePortableSearchShortcut(f.layout, f.boundary), 'failed');
  assert.equal(await fs.readFile(f.shortcut, 'utf8'), 'Concurrent foreign shortcut');
  assert.deepEqual(await fs.readdir(f.programs), ['AHK Manager Portable.lnk']);
});

test('a concurrent change during owned relocation is restored without replacing its new bytes', async (t) => {
  const f = await fixture(t);
  await ensurePortableSearchShortcut(f.layout, f.boundary);
  const relocated = await f.move();
  const rename = fs.rename;
  t.mock.method(fs, 'rename', async (...args: Parameters<typeof fs.rename>) => {
    if (args[0] === f.shortcut) await fs.writeFile(f.shortcut, 'Concurrent replacement');
    return rename(...args);
  });
  assert.equal(await ensurePortableSearchShortcut(relocated, f.boundary), 'preserved');
  assert.equal(await fs.readFile(f.shortcut, 'utf8'), 'Concurrent replacement');
  assert.deepEqual(await fs.readdir(f.programs), ['AHK Manager Portable.lnk']);
});

test('same-size shortcut changes with a restored timestamp preserve the actual changed bytes', async (t) => {
  const f = await fixture(t);
  await ensurePortableSearchShortcut(f.layout, f.boundary);
  const original = (await fs.readFile(f.shortcut, 'utf8')) + '  ';
  await fs.writeFile(f.shortcut, original);
  const fixedTime = new Date('2026-01-01T00:00:00.000Z');
  await fs.utimes(f.shortcut, fixedTime, fixedTime);
  const relocated = await f.move();
  const rename = fs.rename;
  const changed = original.slice(0, -2) + '\t\n';
  assert.equal(Buffer.byteLength(changed), Buffer.byteLength(original));
  t.mock.method(fs, 'rename', async (...args: Parameters<typeof fs.rename>) => {
    if (args[0] === f.shortcut) {
      await fs.writeFile(f.shortcut, changed);
      await fs.utimes(f.shortcut, fixedTime, fixedTime);
    }
    return rename(...args);
  });
  assert.equal(await ensurePortableSearchShortcut(relocated, f.boundary), 'preserved');
  assert.equal(await fs.readFile(f.shortcut, 'utf8'), changed);
  assert.deepEqual(await fs.readdir(f.programs), ['AHK Manager Portable.lnk']);
});

test('a shortcut gaining a second hardlink during shell inspection remains foreign and preserves both names', async (t) => {
  const f = await fixture(t);
  await ensurePortableSearchShortcut(f.layout, f.boundary);
  const original = await fs.readFile(f.shortcut);
  const other = path.join(f.temporary, 'other-shortcut.lnk');
  const read = f.boundary.read;
  let linked = false;
  f.boundary.read = (file) => {
    if (file === f.shortcut && !linked) {
      sync.linkSync(file, other);
      linked = true;
    }
    return read(file);
  };
  assert.equal(await ensurePortableSearchShortcut(f.layout, f.boundary), 'preserved');
  assert.equal(linked, true);
  assert.deepEqual(await fs.readFile(f.shortcut), original);
  assert.deepEqual(await fs.readFile(other), original);
  assert.equal((await fs.lstat(f.shortcut)).nlink, 2);
  assert.equal(f.writes.length, 1);
});
