import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {
  initializePortableStorage,
  PortableInitializationError,
} from '../../src/main/desktop/storage-bootstrap.js';
import type { DeploymentLayout } from '../../src/main/storage/deployment.js';

function layout(): DeploymentLayout {
  const root = path.resolve('Portable manager');
  const data = path.join(root, 'Data');
  return {
    mode: 'portable',
    applicationDirectory: path.join(root, 'App'),
    launchExecutable: path.join(root, 'Bezoro.AhkManager.exe'),
    portableRoot: root,
    configurationPath: path.join(root, 'portable.json'),
    dataDirectory: data,
    profileDirectory: path.join(data, 'Profile'),
    chromiumDirectory: path.join(data, 'Chromium'),
    updatesDirectory: path.join(data, 'Updates'),
    defaultDataDirectory: data,
    legacyDataDirectory: path.resolve('Legacy profile'),
  };
}

const wire = (code: string, phase = 'portable-initialization'): string =>
  `AHK_UPDATE_REFUSAL:{"schemaVersion":1,"phase":"${phase}","code":"${code}"}\n`;
const context = (
  stage = 'protect-root',
  resource = 'root',
  phase = 'portable-initialization',
): string =>
  `AHK_PORTABLE_INITIALIZATION:{"schemaVersion":1,"phase":"${phase}","stage":"${stage}","resource":"${resource}","hresult":"0x80070005","nativeError":null,"userSid":null,"administrator":null}\n`;

function refusal(stderr: string, error?: Error): PortableInitializationError {
  let failure: unknown;
  try {
    initializePortableStorage(layout(), () => ({ status: 65, stderr, error }));
  } catch (caught) {
    failure = caught;
  }
  assert.ok(failure instanceof PortableInitializationError);
  return failure;
}

test('portable initialization uses only its packaged helper and bounds diagnostic capture before browser startup', () => {
  const selected = layout();
  let calls = 0;
  initializePortableStorage(selected, (executable, args, options) => {
    calls++;
    assert.equal(
      executable,
      path.join(selected.applicationDirectory, 'backend', 'update', 'AhkManager.UpdateHost.exe'),
    );
    assert.deepEqual(args, ['--initialize-portable']);
    assert.deepEqual(options, {
      shell: false,
      windowsHide: true,
      stdio: ['ignore', 'ignore', 'pipe'],
      encoding: 'utf8',
      maxBuffer: 4096,
    });
    return { status: 0, stderr: '' };
  });
  assert.equal(calls, 1);
});

test('portable initialization distinguishes NTFS requirements, unavailable cloud files, permissions and package failures', () => {
  const expected: Array<[string, RegExp]> = [
    ['unsupported-filesystem', /requires NTFS.*ReFS, FAT32 and exFAT are not supported/],
    ['cloud-file-unavailable', /available offline.*sync app.*syncing/],
    ['redirected-path', /symbolic link, junction or unsupported redirect/],
    [
      'access-denied',
      /denied access during portable startup.*Copy diagnostic details.*folder or helper/,
    ],
    ['incomplete-package', /package is incomplete.*entire portable ZIP/],
    ['invalid-package', /package or its data settings are invalid.*Data folder and portable.json/],
  ];
  for (const [code, message] of expected) {
    const error = refusal(wire(code));
    assert.equal(error.code, code);
    assert.match(error.message, message);
  }
  for (const code of ['sharing-violation', 'lock-violation']) {
    const error = refusal(wire(code));
    assert.equal(error.code, 'files-in-use');
    assert.match(error.message, /Another application.*sync app finish syncing/);
  }
});

test('invalid or oversized initialization diagnostics never expose native text or paths', () => {
  const valid = wire('access-denied');
  for (const stderr of [
    'Untrusted failure in C:\\Users\\Private\\Secret\n',
    valid.trimEnd(),
    valid.replace('schemaVersion":1', 'schemaVersion":2'),
    valid.replace('"phase":', '"extra":"C:\\\\Private","phase":'),
    valid.replace('"phase":', '"schemaVersion":1,"phase":'),
    valid.replace('portable-initialization', 'installation-ownership'),
    valid.replace('access-denied', 'unknown-code'),
    valid + valid,
    'x'.repeat(4096) + valid,
    valid + 'AHK_UPDATE_REFUSAL:partial',
  ]) {
    const error = refusal(stderr);
    assert.equal(error.code, 'unknown');
    assert.doesNotMatch(error.message, /Secret|Private|Untrusted|AHK_UPDATE_REFUSAL/);
    assert.match(error.message, /existing data was preserved/);
  }
});

test('selected data failures point to the existing data location instead of requesting a new package', () => {
  const phase = 'portable-data-initialization';
  for (const code of ['directory-not-found', 'file-not-found', 'invalid-data', 'invalid-json']) {
    const error = refusal(wire(code, phase));
    assert.equal(error.code, 'data-unavailable');
    assert.match(error.message, /configured data folder.*drive is available.*parent folder exists/);
    assert.doesNotMatch(error.message, /ZIP|package is incomplete/);
  }
  for (const code of ['unsupported-filesystem', 'cloud-file-unavailable', 'access-denied'])
    assert.equal(refusal(wire(code, phase)).code, code);
});

test('helper spawn failures use fixed recovery instructions and never display OS exception messages', () => {
  for (const [code, expected] of [
    ['ENOENT', 'incomplete-package'],
    ['EACCES', 'access-denied'],
    ['EPERM', 'access-denied'],
    ['ENOBUFS', 'unknown'],
    ['EIO', 'unknown'],
  ]) {
    const native = Object.assign(new Error('Private native details and path'), { code });
    const error = refusal(wire('unsupported-filesystem'), native);
    assert.equal(error.code, expected);
    assert.doesNotMatch(error.message, /Private native details/);
  }
  assert.throws(
    () => initializePortableStorage(layout(), () => ({ status: null, stderr: '' })),
    (error) => error instanceof PortableInitializationError && error.code === 'unknown',
  );
  assert.throws(
    () =>
      initializePortableStorage(layout(), () => {
        throw Object.assign(new Error('Private native details and path'), { code: 'EACCES' });
      }),
    (error) =>
      error instanceof PortableInitializationError &&
      error.code === 'access-denied' &&
      !error.message.includes('Private native details'),
  );
});

test('bootstrap diagnostics retain native app and data phases, the failing stage and HRESULT', () => {
  const app = refusal(context() + wire('access-denied'));
  assert.deepEqual(app.diagnostic, {
    origin: 'native',
    phase: 'portable-initialization',
    stage: 'protect-root',
    resource: 'root',
    code: 'access-denied',
    exitCode: 65,
    errorCode: null,
    hresult: '0x80070005',
    nativeError: null,
    userSid: null,
    administrator: null,
  });
  const data = refusal(
    context('verify-data-access', 'data', 'portable-data-initialization') +
      wire('access-denied', 'portable-data-initialization'),
  );
  assert.equal(data.diagnostic.phase, 'portable-data-initialization');
  assert.equal(data.diagnostic.stage, 'verify-data-access');
  assert.equal(data.diagnostic.resource, 'data');
  const win32 = refusal(
    context().replace('"nativeError":null', '"nativeError":5') + wire('access-denied'),
  );
  assert.equal(win32.diagnostic.nativeError, 5);
  const legacy = refusal(wire('access-denied'));
  assert.equal(legacy.diagnostic.origin, 'native');
  assert.equal(legacy.diagnostic.phase, 'portable-initialization');
  assert.equal(legacy.diagnostic.stage, null);
  assert.equal(legacy.diagnostic.hresult, null);
});

test('spawn diagnostics are distinct from native permission refusals and contain no raw OS messages', () => {
  const error = refusal(
    context() + wire('access-denied'),
    Object.assign(new Error('token=private-native-value C:\\Private'), { code: 'EPERM' }),
  );
  assert.deepEqual(error.diagnostic, {
    origin: 'spawn',
    phase: null,
    stage: null,
    resource: 'helper',
    code: 'access-denied',
    exitCode: 65,
    errorCode: 'EPERM',
    hresult: null,
    nativeError: null,
    userSid: null,
    administrator: null,
  });
  assert.doesNotMatch(JSON.stringify(error.diagnostic), /private|Private|token/);
  const unrecognized = refusal('', Object.assign(new Error('secret'), { code: 'secret-code' }));
  assert.equal(unrecognized.diagnostic.errorCode, null);
});

test('untrusted, duplicated, mismatched or partial bootstrap context is ignored without hiding the finite refusal', () => {
  const valid = context();
  for (const invalid of [
    valid.trimEnd(),
    valid + valid,
    valid.replace('schemaVersion":1', 'schemaVersion":2'),
    valid.replace('"stage":', '"stage":"private-value","stage":'),
    valid.replace('"stage":', '"extra":"token=private-value","stage":'),
    valid.replace('protect-root', 'unknown-stage'),
    valid.replace('"root"', '"private-resource"'),
    valid.replace('0x80070005', 'C:\\Private\\secret'),
    valid.replace('0x80070005', '0x8007000a'),
    valid.replace('"nativeError":null', '"nativeError":65536'),
    valid.replace('"nativeError":null', '"nativeError":-5'),
    valid.replace('"nativeError":null', '"nativeError":"private-value"'),
    valid.replace('"userSid":null', '"userSid":"private-token"'),
    valid.replace('"userSid":null', '"userSid":"S-1' + '-1'.repeat(16) + '"'),
    valid.replace('"userSid":null', '"userSid":"S-' + '1'.repeat(185) + '-5"'),
    valid.replace('"administrator":null', '"administrator":"true"'),
    context('verify-data-access', 'data', 'portable-data-initialization'),
  ]) {
    const error = refusal(wire('access-denied') + invalid);
    assert.equal(error.code, 'access-denied');
    assert.equal(error.diagnostic.stage, null);
    assert.equal(error.diagnostic.resource, null);
    assert.equal(error.diagnostic.hresult, null);
    assert.doesNotMatch(JSON.stringify(error.diagnostic), /private|Private|secret|token/);
  }
  const overflow = refusal(wire('access-denied') + valid + 'ø'.repeat(4096));
  assert.equal(overflow.diagnostic.origin, 'unknown');
  assert.equal(overflow.diagnostic.stage, null);
  const crlf = refusal((wire('access-denied') + valid).replaceAll('\n', '\r\n'));
  assert.equal(crlf.diagnostic.stage, 'protect-root');
  const subject = refusal(
    (wire('access-denied') + valid)
      .replace('"userSid":null', '"userSid":"S-1-5-21-100-200-300-1001"')
      .replace('"administrator":null', '"administrator":true'),
  );
  assert.equal(subject.diagnostic.userSid, 'S-1-5-21-100-200-300-1001');
  assert.equal(subject.diagnostic.administrator, true);
});

test('bootstrap context cannot turn an unsuccessful initializer into success or affect successful startup', () => {
  assert.throws(
    () => initializePortableStorage(layout(), () => ({ status: null, stderr: context() })),
    (error) =>
      error instanceof PortableInitializationError &&
      error.code === 'unknown' &&
      error.diagnostic.phase === null,
  );
  initializePortableStorage(layout(), () => ({
    status: 0,
    stderr: context() + wire('access-denied'),
  }));
});
