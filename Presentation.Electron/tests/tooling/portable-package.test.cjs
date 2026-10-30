const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { test, afterEach } = require('node:test');
const { stagePortablePackage, LIMITS } = require('../../scripts/pack-portable.cjs');

const roots = [];
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-portable-package-'));
  roots.push(root);
  const sourceRoot = path.join(root, 'source');
  const launcherPath = path.join(root, 'launcher.exe');
  const outputRoot = path.join(root, 'output');
  for (const relative of [
    'Bezoro.AhkManager.exe',
    'resources/app.asar',
    'resources/app-update.yml',
    'backend/AhkManager.Backend.exe',
    'backend/update/AhkManager.UpdateHost.exe',
    'locales/en-US.pak',
  ]) {
    const file = path.join(sourceRoot, relative);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, `exact fixture bytes: ${relative}`);
  }
  fs.writeFileSync(launcherPath, 'native launcher fixture');
  return {
    root,
    sourceRoot,
    launcherPath,
    outputRoot,
    version: '1.4.0-test.37.1.6',
    commit: 'a'.repeat(40),
  };
}

test('portable inventory binds every copied runtime file and excludes mutable user data', () => {
  const value = fixture();
  fs.writeFileSync(path.join(value.sourceRoot, 'empty-license'), '');
  const result = stagePortablePackage(value);
  assert.equal(result.applicationId, 'Bezoro.AhkManager.Electron');
  assert.equal(result.updateProtocol, 2);
  assert.equal(result.version, value.version);
  assert.equal(result.commit, value.commit);
  assert.equal(result.files.length, 8);
  assert.deepEqual(
    result.files.map((file) => file.path),
    result.files.map((file) => file.path).sort(),
  );
  for (const file of result.files) {
    const bytes = fs.readFileSync(path.join(value.outputRoot, file.path));
    assert.equal(bytes.length, file.bytes);
    assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'), file.sha256);
  }
  assert.deepEqual(
    JSON.parse(fs.readFileSync(path.join(value.outputRoot, 'portable.json'), 'utf8')),
    { schemaVersion: 1, applicationId: 'Bezoro.AhkManager.Electron', dataDirectory: 'Data' },
  );
  assert.deepEqual(
    JSON.parse(fs.readFileSync(path.join(value.outputRoot, 'portable-package.json'), 'utf8')),
    result,
  );
  assert.equal(fs.existsSync(path.join(value.outputRoot, 'Data')), false);
  assert.equal(
    result.files.some(
      (file) => file.path === 'portable.json' || file.path === 'portable-package.json',
    ),
    false,
  );
});

test('packaging preserves source bytes and refuses an existing or nested destination', () => {
  const value = fixture();
  const original = fs.readFileSync(path.join(value.sourceRoot, 'Bezoro.AhkManager.exe'));
  stagePortablePackage(value);
  assert.deepEqual(fs.readFileSync(path.join(value.sourceRoot, 'Bezoro.AhkManager.exe')), original);
  assert.throws(() => stagePortablePackage(value), /fresh/);
  assert.throws(
    () => stagePortablePackage({ ...value, outputRoot: path.join(value.sourceRoot, 'nested') }),
    /outside/,
  );
});

test('release identity and required runtime files are mandatory', () => {
  for (const change of [
    { version: '1.4.0-beta.1' },
    { commit: 'main' },
    { commit: 'A'.repeat(40) },
  ]) {
    const value = fixture();
    assert.throws(() => stagePortablePackage({ ...value, ...change }), /exact release/);
  }
  for (const relative of [
    'Bezoro.AhkManager.exe',
    'resources/app.asar',
    'resources/app-update.yml',
    'backend/update/AhkManager.UpdateHost.exe',
  ]) {
    const value = fixture();
    fs.rmSync(path.join(value.sourceRoot, relative));
    assert.throws(() => stagePortablePackage(value), /incomplete/);
  }
  const value = fixture();
  fs.writeFileSync(value.launcherPath, '');
  assert.throws(() => stagePortablePackage(value), /launcher is empty/);
});

test('staging refuses accidentally bundled user Data and oversized payload files', () => {
  const value = fixture();
  fs.mkdirSync(path.join(value.sourceRoot, 'Data'));
  fs.writeFileSync(path.join(value.sourceRoot, 'Data', 'private.json'), 'user settings');
  assert.throws(() => stagePortablePackage(value), /User Data/);
  const huge = fixture();
  const file = fs.openSync(path.join(huge.sourceRoot, 'too-large.bin'), 'wx');
  fs.ftruncateSync(file, LIMITS.fileBytes + 1);
  fs.closeSync(file);
  assert.throws(() => stagePortablePackage(huge), /bounded regular/);
});

test('missing or empty operational backend cannot produce a portable package', () => {
  const missing = fixture();
  fs.rmSync(path.join(missing.sourceRoot, 'backend/AhkManager.Backend.exe'));
  assert.throws(
    () => stagePortablePackage(missing),
    /incomplete: App\/backend\/AhkManager\.Backend\.exe/,
  );
  assert.equal(fs.existsSync(missing.outputRoot), false);
  const empty = fixture();
  fs.writeFileSync(path.join(empty.sourceRoot, 'backend/AhkManager.Backend.exe'), '');
  assert.throws(
    () => stagePortablePackage(empty),
    /runtime file is empty: App\/backend\/AhkManager\.Backend\.exe/,
  );
  assert.equal(fs.existsSync(empty.outputRoot), false);
});

test('hard-linked payloads cannot enter a portable package', () => {
  const value = fixture();
  fs.linkSync(
    path.join(value.sourceRoot, 'Bezoro.AhkManager.exe'),
    path.join(value.root, 'another-link.exe'),
  );
  assert.throws(() => stagePortablePackage(value), /single-link/);
});

test(
  'redirected directories and case-colliding Windows names are rejected',
  { skip: process.platform === 'win32' },
  () => {
    const redirected = fixture();
    fs.symlinkSync(
      path.join(redirected.sourceRoot, 'resources'),
      path.join(redirected.sourceRoot, 'redirected'),
      'dir',
    );
    assert.throws(() => stagePortablePackage(redirected), /redirected/);
    const collision = fixture();
    fs.writeFileSync(path.join(collision.sourceRoot, 'LOCALes'), 'colliding file');
    assert.throws(() => stagePortablePackage(collision), /collide/);
    const casing = fixture();
    fs.mkdirSync(path.join(casing.sourceRoot, 'BACKEND'));
    fs.writeFileSync(path.join(casing.sourceRoot, 'BACKEND', 'other.txt'), 'other');
    assert.throws(() => stagePortablePackage(casing), /differently cased/);
  },
);
