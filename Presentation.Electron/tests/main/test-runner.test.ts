import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const requireTool = createRequire(path.resolve('package.json'));
const { testInputs, discoverTests } = requireTool('./scripts/lib/test-inputs.cjs') as {
  testInputs(args: string[]): { output: string; namePattern?: string };
  discoverTests(directory: string): string[];
};

test('Node test discovery includes feature subdirectories in deterministic order', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-test-discovery-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const entries = ['renderer/z.test.tsx', 'main/nested/b.test.ts', 'main/a.test.ts'];
  for (const name of [...entries, 'main/fixture.ts', 'tooling/separate.test.cjs']) {
    const file = path.join(root, name);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, '');
  }
  assert.deepEqual(
    discoverTests(root),
    entries.sort().map((name) => path.join(root, name)),
  );
});

test('focused Node test runs retain fresh output requirements and reject unsupported options', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-test-inputs-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const output = path.join(root, 'new-evidence');
  assert.deepEqual(testInputs(['--out', output, '--test-name-pattern', '^agent']), {
    output,
    namePattern: '^agent',
  });
  assert.deepEqual(testInputs(['--out', output]), { output, namePattern: undefined });
  assert.throws(() => testInputs(['--out', root]), /fresh absolute/);
  assert.throws(() => testInputs(['--out', 'relative']), /fresh absolute/);
  assert.throws(() => testInputs(['--out', output, '--other']), /Unknown option/);
  assert.throws(() => testInputs(['--out', output, '--test-name-pattern', '[']), SyntaxError);
  assert.equal(fs.existsSync(output), false, 'argument validation must not create test output');
});
