import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcess } from 'node:child_process';
import { resolveDeployment } from '../../src/main/storage/deployment.js';
import { completePendingDataMove, prepareDataMove } from '../../src/main/storage/data-migration.js';
import { withDataMoveLease } from '../../src/main/storage/data-move-lease.js';

async function fixture(t: TestContext) {
  const temporary = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(temporary && path.isAbsolute(temporary));
  const parent = await fs.mkdtemp(path.join(temporary, 'storage-lease-'));
  t.after(() => fs.rm(parent, { recursive: true, force: true }));
  const root = path.join(parent, 'Portable');
  const executable = path.join(root, 'App', 'Bezoro.AhkManager.exe');
  await fs.mkdir(path.dirname(executable), { recursive: true });
  const marker = path.join(root, 'portable.json');
  const initial = JSON.stringify({
    schemaVersion: 1,
    applicationId: 'Bezoro.AhkManager.Electron',
    dataDirectory: 'Data',
  });
  await fs.writeFile(marker, initial);
  const layout = resolveDeployment(executable, parent);
  await fs.mkdir(layout.profileDirectory, { recursive: true });
  await fs.writeFile(path.join(layout.profileDirectory, 'settings.json'), '{"fixture":"source"}');
  const target = path.join(parent, 'Selected data');
  const prepare = () =>
    prepareDataMove(layout, target, async (directory) => {
      await fs.mkdir(directory, { mode: 0o700 });
    });
  return { parent, root, marker, initial, layout, target, prepare };
}

function nativeFixture(options: { ready?: string; release?: string } = {}) {
  const events: string[] = [];
  const child = new EventEmitter() as ChildProcess;
  const input = new PassThrough();
  const output = new PassThrough();
  Object.assign(child, { stdin: input, stdout: output, exitCode: null });
  const exit = (code: number) => {
    if (child.exitCode !== null) return;
    Object.assign(child, { exitCode: code });
    events.push('exit:' + code);
    child.emit('exit', code, null);
  };
  input.on('data', (chunk: Buffer) => {
    events.push(chunk.toString().trim());
    output.write((options.release ?? '{"stageReleased":true}') + '\n');
  });
  input.on('end', () => {
    events.push('eof');
    exit(0);
  });
  const start = (helper: string): ChildProcess => {
    events.push(helper);
    queueMicrotask(() => output.write((options.ready ?? '{"ready":true}') + '\n'));
    return child;
  };
  return { child, events, start, exit };
}

test('a Data move retains the native lease until pointer commit and releases staging only for promotion', async (t) => {
  const f = await fixture(t);
  await f.prepare();
  const native = nativeFixture();
  const relocated = await withDataMoveLease(
    f.layout,
    async (lease) => {
      native.events.push('copy');
      const result = await completePendingDataMove(f.layout, lease);
      native.events.push('committed');
      assert.equal(
        resolveDeployment(path.join(f.root, 'App', 'Bezoro.AhkManager.exe'), f.parent)
          .dataDirectory,
        f.target,
      );
      assert.equal(native.child.exitCode, null);
      return result;
    },
    native.start,
  );
  assert.equal(relocated.dataDirectory, f.target);
  assert.deepEqual(native.events, [
    path.join(f.layout.applicationDirectory, 'backend', 'update', 'AhkManager.UpdateHost.exe'),
    'copy',
    '{"releaseStage":true}',
    'committed',
    'eof',
    'exit:0',
  ]);
  assert.equal(
    await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
    '{"fixture":"source"}',
  );
});

test('no native move lease starts when there is no pending relocation', async (t) => {
  const f = await fixture(t);
  const result = await withDataMoveLease(
    f.layout,
    (lease) => completePendingDataMove(f.layout, lease),
    () => {
      throw new Error('Unexpected helper');
    },
  );
  assert.equal(result, f.layout);
});

test('a malformed native lease response prevents all copying and preserves the pointer', async (t) => {
  const f = await fixture(t);
  await f.prepare();
  const native = nativeFixture({ ready: '{"ready":true,"extra":true}' });
  let called = false;
  await assert.rejects(
    withDataMoveLease(
      f.layout,
      async () => {
        called = true;
      },
      native.start,
    ),
    /response was invalid/,
  );
  assert.equal(called, false);
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  assert.equal(native.events.at(-2), 'eof');
});

test('native refusal before admission never starts the move', async (t) => {
  const f = await fixture(t);
  await f.prepare();
  const native = nativeFixture();
  let copied = false;
  await assert.rejects(
    withDataMoveLease(
      f.layout,
      async () => {
        copied = true;
      },
      () => {
        queueMicrotask(() => native.exit(65));
        return native.child;
      },
    ),
    /lease ended unexpectedly/,
  );
  assert.equal(copied, false);
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
});

test('an unexpectedly ended native lease stops the next copy effect without committing a new Data pointer', async (t) => {
  const f = await fixture(t);
  await f.prepare();
  const native = nativeFixture();
  const originalCopy = fs.copyFile.bind(fs);
  t.mock.method(fs, 'copyFile', async (...args: Parameters<typeof fs.copyFile>) => {
    await originalCopy(...args);
    native.exit(70);
  });
  await assert.rejects(
    withDataMoveLease(f.layout, (lease) => completePendingDataMove(f.layout, lease), native.start),
    /lease ended unexpectedly/,
  );
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  assert.equal(native.events.includes('{"releaseStage":true}'), false);
  assert.equal(
    await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
    '{"fixture":"source"}',
  );
});

test('a missing stage-release acknowledgement prevents directory promotion', async (t) => {
  const f = await fixture(t);
  await f.prepare();
  const native = nativeFixture({ release: '{"stageReleased":false}' });
  await assert.rejects(
    withDataMoveLease(f.layout, (lease) => completePendingDataMove(f.layout, lease), native.start),
    /response was invalid/,
  );
  assert.equal(await fs.readFile(f.marker, 'utf8'), f.initial);
  await assert.rejects(fs.stat(f.target), { code: 'ENOENT' });
});
