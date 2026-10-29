import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createDiagnostics, type DiagnosticBoundary } from '../../src/main/diagnostics.js';

class Timers {
  time = 0;
  tasks = new Set<{ at: number; fn: () => void }>();
  schedule = (fn: () => void, ms: number): (() => void) => {
    const task = { at: this.time + ms, fn };
    this.tasks.add(task);
    return () => {
      this.tasks.delete(task);
    };
  };
  advance(ms: number): void {
    this.time += ms;
    for (const task of [...this.tasks].sort((a, b) => a.at - b.at))
      if (task.at <= this.time && this.tasks.delete(task)) task.fn();
  }
}
const flush = async () => {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
};
function fixture() {
  const root = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  if (!root || !path.isAbsolute(root))
    throw new Error('Supply an isolated absolute AHK_DIAGNOSTICS_TEST_ROOT');
  const parent = fs.mkdtempSync(path.join(root, 'ahk-diagnostics-'));
  return { parent, dir: path.join(parent, 'fresh') };
}
const fake = (): DiagnosticBoundary => ({
  capture: async () => Buffer.from('fake PNG, no window'),
  summary: () => ({ revision: '9007199254740993', selectedId: 'opaque-id' }),
});

test('normal launch performs no diagnostic IO or boundary calls', () => {
  let called = false;
  assert.equal(
    createDiagnostics([], {
      capture: async () => {
        called = true;
        return Buffer.alloc(0);
      },
      summary: () => {
        called = true;
        return {};
      },
    }),
    null,
  );
  assert.equal(called, false);
});
test('opt-in refuses relative, duplicate, unnormalized and existing destinations without overwrite', () => {
  const { parent, dir } = fixture();
  for (const args of [
    ['--diagnostics-dir=relative'],
    ['--diagnostics-dir'],
    ['--diagnostics-dir=' + dir, '--diagnostics-dir=' + dir],
    ['--diagnostics-dir=' + dir + path.sep + '..' + path.sep + 'other'],
  ])
    assert.throws(() => createDiagnostics(args, fake()));
  fs.mkdirSync(dir);
  fs.writeFileSync(path.join(dir, 'original'), 'preserve');
  assert.throws(() => createDiagnostics(['--diagnostics-dir=' + dir], fake()));
  assert.equal(fs.readFileSync(path.join(dir, 'original'), 'utf8'), 'preserve');
  fs.rmSync(parent, { recursive: true });
});
test('only initial-ready and first non-null selection capture after delay', async () => {
  const { parent, dir } = fixture(),
    timers = new Timers();
  let captures = 0;
  const diagnostic = createDiagnostics(
    ['--diagnostics-dir=' + dir],
    {
      ...fake(),
      capture: async () => {
        captures++;
        return Buffer.from('PNG');
      },
    },
    { schedule: timers.schedule },
  )!;
  diagnostic.selectionCommitted(null);
  diagnostic.initialReady();
  diagnostic.initialReady();
  diagnostic.selectionCommitted('first');
  diagnostic.selectionCommitted('second');
  timers.advance(499);
  await flush();
  assert.equal(captures, 0);
  timers.advance(1);
  await flush();
  assert.equal(captures, 2);
  assert.deepEqual(
    fs
      .readdirSync(dir)
      .filter((n) => n.endsWith('.png'))
      .sort(),
    ['first-selection.png', 'initial-ready.png'],
  );
  diagnostic.close();
  fs.rmSync(parent, { recursive: true });
});
test('capture deadline records unknown result and suppresses a late completed image', async () => {
  const { parent, dir } = fixture(),
    timers = new Timers();
  let finish!: (value: Buffer) => void;
  const diagnostic = createDiagnostics(
    ['--diagnostics-dir=' + dir],
    {
      ...fake(),
      capture: () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    },
    { schedule: timers.schedule, delayMs: 0, deadlineMs: 10 },
  )!;
  diagnostic.initialReady();
  timers.advance(0);
  await flush();
  timers.advance(10);
  finish(Buffer.from('late'));
  await flush();
  assert.equal(fs.existsSync(path.join(dir, 'initial-ready.png')), false);
  assert.match(fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8'), /capture-timeout/);
  diagnostic.close();
  fs.rmSync(parent, { recursive: true });
});
test('close cancels deferred captures and suppresses in-flight results', async () => {
  const { parent, dir } = fixture(),
    timers = new Timers();
  let finish!: (value: Buffer) => void;
  const diagnostic = createDiagnostics(
    ['--diagnostics-dir=' + dir],
    {
      ...fake(),
      capture: () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    },
    { schedule: timers.schedule, delayMs: 0 },
  )!;
  diagnostic.initialReady();
  timers.advance(0);
  await flush();
  diagnostic.selectionCommitted('first');
  diagnostic.close();
  finish(Buffer.from('late'));
  timers.advance(10000);
  await flush();
  assert.equal(
    fs.readdirSync(dir).some((n) => n.endsWith('.png')),
    false,
  );
  assert.equal(timers.tasks.size, 0);
  fs.rmSync(parent, { recursive: true });
});
test('logs bound message and total bytes, redact broker-shaped tokens, and cap images', async () => {
  const { parent, dir } = fixture(),
    timers = new Timers();
  const diagnostic = createDiagnostics(
    ['--diagnostics-dir=' + dir],
    { ...fake(), capture: async () => Buffer.alloc(8 * 1024 * 1024 + 1) },
    { schedule: timers.schedule, delayMs: 0, maxLogBytes: 4096 },
  )!;
  diagnostic.record('console', { message: 'a'.repeat(64) + 'Z'.repeat(5000) });
  diagnostic.initialReady();
  timers.advance(0);
  await flush();
  assert.equal(fs.existsSync(path.join(dir, 'initial-ready.png')), false);
  for (let i = 0; i < 50; i++) diagnostic.record('console', { message: 'Z'.repeat(5000) });
  const log = fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8');
  assert.ok(Buffer.byteLength(log) <= 4096);
  assert.ok(!log.includes('a'.repeat(64)));
  assert.ok(log.includes('[redacted-token]'));
  assert.match(log, /capture-refused/);
  diagnostic.close();
  fs.rmSync(parent, { recursive: true });
});
test('failed capture is recorded once without retry', async () => {
  const { parent, dir } = fixture(),
    timers = new Timers();
  let calls = 0;
  const diagnostic = createDiagnostics(
    ['--diagnostics-dir=' + dir],
    {
      ...fake(),
      capture: async () => {
        calls++;
        throw new Error('fake capture failure');
      },
    },
    { schedule: timers.schedule, delayMs: 0 },
  )!;
  diagnostic.initialReady();
  timers.advance(0);
  await flush();
  timers.advance(10000);
  await flush();
  assert.equal(calls, 1);
  assert.match(fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8'), /capture-error/);
  diagnostic.close();
  fs.rmSync(parent, { recursive: true });
});
test('unavailable summary and rejected capture remain diagnostic errors without throwing', async () => {
  const { parent, dir } = fixture(),
    timers = new Timers();
  const diagnostic = createDiagnostics(
    ['--diagnostics-dir=' + dir],
    {
      summary: () => {
        throw new Error('destroyed');
      },
      capture: async () => {
        throw new Error('destroyed');
      },
    },
    { schedule: timers.schedule, delayMs: 0 },
  )!;
  diagnostic.initialReady();
  assert.doesNotThrow(() => timers.advance(0));
  await flush();
  assert.match(fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8'), /unavailable/);
  diagnostic.close();
  fs.rmSync(parent, { recursive: true });
});
