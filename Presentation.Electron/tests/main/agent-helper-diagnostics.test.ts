import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import childProcess, { type ChildProcessWithoutNullStreams } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { syncBuiltinESMExports } from 'node:module';
import {
  AgentTransportError,
  agentHelperCodes,
  observeAgentHelperDiagnostics,
  readAgentHelperDiagnostic,
} from '../../src/main/agent-helper-diagnostics.js';
import { agentFrame, startAgentTransport } from '../../src/main/agent-transport.js';
import { CommandLog } from '../../src/main/logging/command-log.js';

const wire = 'AHK_AGENT_REFUSAL:{"schemaVersion":1,"phase":"relay","code":"InvalidInput"}\n';

test('native agent diagnostics accept only complete finite records', () => {
  for (const code of agentHelperCodes)
    for (const phase of ['server', 'relay'] as const) {
      const line = `AHK_AGENT_REFUSAL:{"schemaVersion":1,"phase":"${phase}","code":"${code}"}\n`;
      assert.deepEqual(readAgentHelperDiagnostic(line), { schemaVersion: 1, phase, code });
    }
  for (const invalid of [
    wire.trimEnd(),
    wire.replace('relay', 'private-phase'),
    wire.replace('InvalidInput', 'private-token'),
    wire.replace('"phase":', '"schemaVersion":1,"phase":'),
    wire + wire,
    wire.replace('"phase":', '"secret":"private","phase":'),
    'x'.repeat(513),
  ])
    assert.equal(readAgentHelperDiagnostic(invalid), null);
});

test('agent stderr drains arbitrary and oversized records but retains subsequent finite failures', () => {
  const stream = new PassThrough();
  const errors: Error[] = [];
  const observed = observeAgentHelperDiagnostics(stream, (error) => errors.push(error));
  stream.write(wire.slice(0, 32));
  assert.equal(observed(), false);
  stream.write(wire.slice(32));
  stream.write('private-token' + 'x'.repeat(8192) + '\n');
  stream.write('arbitrary private stderr\n');
  stream.end(wire.replace('InvalidInput', 'TransportFailure'));
  assert.equal(observed(), true);
  assert.equal(errors.length, 2);
  assert.ok(errors.every((error) => error instanceof AgentTransportError));
  assert.deepEqual(
    errors.map((error) => (error as AgentTransportError).code),
    ['InvalidInput', 'TransportFailure'],
  );
  assert.doesNotMatch(JSON.stringify(errors), /private|arbitrary/);
});

test('native agent finite failures survive error copy and export without raw stderr or request contents', async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-agent-native-diagnostics-'));
  const log = new CommandLog(directory);
  t.after(() => {
    log.close();
    fs.rmSync(directory, { recursive: true, force: true });
  });
  const packet = readAgentHelperDiagnostic(wire)!;
  await assert.rejects(
    log.run('lifecycle', 'agentChild', () => {
      throw new AgentTransportError(packet);
    }),
  );
  const copied = log.snapshot('errors').records.at(-1)!;
  assert.equal(copied.command, 'agentChild');
  assert.equal(copied.errorName, 'AgentTransportError');
  assert.equal(copied.errorCode, 'InvalidInput');
  assert.equal(copied.helperPhase, 'relay');
  assert.equal(copied.helperCode, 'InvalidInput');
  assert.deepEqual(log.exportFiles().issues, []);
  const secret = 'github_pat_private-native-diagnostic';
  await assert.rejects(
    log.run('lifecycle', 'agentChild', () => {
      throw new AgentTransportError({ schemaVersion: 1, phase: 'relay', code: secret });
    }),
  );
  assert.doesNotMatch(JSON.stringify(log.snapshot('errors')), /private-native-diagnostic/);
  assert.doesNotMatch(JSON.stringify(log.exportFiles()), /private-native-diagnostic/);
});

test('a failed diagnostic sink does not interrupt subsequent agent protocol observations', () => {
  const stream = new PassThrough();
  let calls = 0;
  observeAgentHelperDiagnostics(stream, () => {
    calls++;
    throw new Error('unavailable sink');
  });
  assert.doesNotThrow(() => stream.end(wire + wire));
  assert.equal(calls, 2);
});

test('an unexpectedly exiting owned relay produces a finite failure instead of disappearing silently', async () => {
  const failures: unknown[] = [];
  let observed!: () => void;
  const failed = new Promise<void>((resolve) => {
    observed = resolve;
  });
  // Node is an inert local child: --server is invalid, so it exits without
  // starting a real Manager, script, listener, or private protocol endpoint.
  const close = startAgentTransport(
    process.execPath,
    { version: '2.0.2', generation: 'fixture' },
    async () => assert.fail('No command should be dispatched'),
    (error) => {
      failures.push(error);
      observed();
    },
  );
  try {
    await failed;
    assert.equal(failures.length, 1);
    assert.ok(failures[0] instanceof AgentTransportError);
    assert.equal((failures[0] as AgentTransportError).code, 'TransportFailure');
  } finally {
    await close();
  }
});

test('normal owned relay closure does not report its expected process exit as a failure', async () => {
  const failures: unknown[] = [];
  const close = startAgentTransport(
    process.execPath,
    { version: '2.0.2', generation: 'fixture' },
    async () => assert.fail('No command should be dispatched'),
    (error) => failures.push(error),
  );
  await close();
  assert.deepEqual(failures, []);
});

for (const preceding of ['dispatch', 'relay'] as const)
  test(`a prior ${preceding} failure cannot hide a later unexpected relay exit`, async (t) => {
    const helper = Object.assign(new EventEmitter(), {
      stdin: new PassThrough(),
      stdout: new PassThrough(),
      stderr: new PassThrough(),
      exitCode: null as number | null,
      kill: () => true,
    });
    const spawn = t.mock.method(
      childProcess,
      'spawn',
      () => helper as unknown as ChildProcessWithoutNullStreams,
    );
    syncBuiltinESMExports();
    const failures: unknown[] = [];
    const close = startAgentTransport(
      'inert-helper',
      { version: '2.0.2', generation: 'fixture' },
      async () => {
        throw new Error('A command failed while the relay remained available.');
      },
      (error) => failures.push(error),
    );
    try {
      if (preceding === 'relay') helper.stderr.write(wire);
      else
        helper.stdout.write(
          agentFrame({
            protocol: 1,
            requestId: '8f1dc2e4-493d-4a97-b7a4-fcb3b276619c',
            command: 'app.status',
          }),
        );
      await new Promise<void>((resolve) => setImmediate(resolve));
      assert.equal(failures.length, 1);
      helper.exitCode = 65;
      helper.emit('close', 65, null);
      assert.equal(failures.length, 2);
      assert.equal((failures[1] as AgentTransportError).code, 'TransportFailure');
    } finally {
      await close();
      spawn.mock.restore();
      syncBuiltinESMExports();
    }
  });

test('a terminal native server refusal is retained without a duplicate exit fallback', async (t) => {
  const helper = Object.assign(new EventEmitter(), {
    stdin: new PassThrough(),
    stdout: new PassThrough(),
    stderr: new PassThrough(),
    exitCode: null as number | null,
    kill: () => true,
  });
  const spawn = t.mock.method(
    childProcess,
    'spawn',
    () => helper as unknown as ChildProcessWithoutNullStreams,
  );
  syncBuiltinESMExports();
  const failures: unknown[] = [];
  const close = startAgentTransport(
    'inert-helper',
    { version: '2.0.2', generation: 'fixture' },
    async () => assert.fail('No command should be dispatched'),
    (error) => failures.push(error),
  );
  try {
    helper.stderr.write(wire.replace('relay', 'server').replace('InvalidInput', 'AccessDenied'));
    helper.exitCode = 65;
    helper.emit('close', 65, null);
    assert.equal(failures.length, 1);
    assert.equal((failures[0] as AgentTransportError).code, 'AccessDenied');
  } finally {
    await close();
    spawn.mock.restore();
    syncBuiltinESMExports();
  }
});
