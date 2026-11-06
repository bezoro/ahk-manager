const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { NtExecutable, NtExecutableResource, Resource } = require('resedit');
const { verifyPortableExecutable } = require('../../scripts/verify-portable-executable.cjs');

function image(
  root,
  name,
  version,
  level = 'requireAdministrator',
  access = 'false',
  utf16 = false,
) {
  const executable = NtExecutable.createEmpty(false);
  const resources = NtExecutableResource.from(executable);
  Resource.VersionInfo.create({
    lang: 0x0409,
    fixedInfo: {},
    strings: [
      {
        lang: 0x0409,
        codepage: 1200,
        values: { FileVersion: version, ProductVersion: version.split('-')[0] + '.0' },
      },
    ],
  }).outputToResourceEntries(resources.entries);
  const xml =
    '<?xml version="1.0"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1">' +
    `<requestedExecutionLevel level="${level}" uiAccess="${access}" /></assembly>`;
  const raw = utf16 ? Buffer.from('\ufeff' + xml, 'utf16le') : Buffer.from(xml, 'utf8');
  resources.entries.push({
    type: 24,
    id: 1,
    lang: 0x0409,
    codepage: 65001,
    bin: Uint8Array.from(raw).buffer,
  });
  resources.outputResource(executable);
  const file = path.join(root, name + '.exe');
  fs.writeFileSync(file, Buffer.from(executable.generate()));
  return file;
}

function temporary(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-portable-pe-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}

test('final PE inspection verifies stable and preview identity without executing or changing images', (t) => {
  const root = temporary(t);
  for (const [index, version] of ['2.0.0', '2.0.1-test.19.63.1'].entries()) {
    const file = image(root, String(index), version, 'requireAdministrator', 'false', index === 1);
    const original = fs.readFileSync(file);
    const result = verifyPortableExecutable(file, version);
    assert.equal(result.requestedExecutionLevel, 'requireAdministrator');
    assert.equal(result.uiAccess, false);
    assert.equal(result.readOnly, true);
    assert.equal(result.fileVersion, version);
    assert.equal(result.bytes, original.length);
    assert.match(result.sha256, /^[0-9a-f]{64}$/);
    assert.deepEqual(fs.readFileSync(file), original);
  }
});

test('final PE inspection refuses standard privileges, UIAccess and a lost preview identity', (t) => {
  const root = temporary(t);
  assert.throws(
    () => verifyPortableExecutable(image(root, 'normal', '2.0.0', 'asInvoker'), '2.0.0'),
    /administrator access/,
  );
  assert.throws(
    () =>
      verifyPortableExecutable(
        image(root, 'uiaccess', '2.0.0', 'requireAdministrator', 'true'),
        '2.0.0',
      ),
    /administrator access/,
  );
  assert.throws(
    () => verifyPortableExecutable(image(root, 'wrong-preview', '2.0.0'), '2.0.0-test.19.63.1'),
    /release identity/,
  );
});

test('PE verification CLI writes fresh evidence and preserves an existing receipt on refusal', (t) => {
  const root = temporary(t);
  const file = image(root, 'candidate', '2.0.0');
  const out = path.join(root, 'verification.json');
  const args = [
    path.resolve('scripts/verify-portable-executable.cjs'),
    '--file',
    file,
    '--version',
    '2.0.0',
    '--out',
    out,
  ];
  assert.equal(spawnSync(process.execPath, args, { encoding: 'utf8' }).status, 0);
  const original = fs.readFileSync(out);
  assert.equal(JSON.parse(original).requestedExecutionLevel, 'requireAdministrator');
  assert.notEqual(spawnSync(process.execPath, args, { encoding: 'utf8' }).status, 0);
  assert.deepEqual(fs.readFileSync(out), original);
});
