const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { parseArgs } = require('node:util');
// Node 24's type stripper reads the production parser without introducing a shared
// runtime trust validator. Each process keeps enforcing its own boundary.
const { readUpdateHostRefusal } = require('../src/main/updates/update-host-diagnostics.ts');

function verifyUpdateHostDiagnostics(file) {
  if (!path.isAbsolute(file)) throw new Error('Use an absolute native diagnostic fixture path.');
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > 256 * 1024)
    throw new Error('A bounded native diagnostic fixture is required.');
  const lines = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (!Array.isArray(lines) || !lines.length || lines.length > 4096)
    throw new Error('The native diagnostic fixture is incomplete.');
  const phases = new Set();
  const codes = new Set();
  for (const line of lines) {
    if (typeof line !== 'string' || line.length > 512 || !line.startsWith('AHK_UPDATE_REFUSAL:'))
      throw new Error('The native diagnostic fixture contains an invalid record.');
    const expected = JSON.parse(line.slice('AHK_UPDATE_REFUSAL:'.length));
    const actual = readUpdateHostRefusal(Buffer.from(line + '\n'));
    assert.deepEqual(actual, expected, 'The TypeScript parser rejected a native refusal contract.');
    phases.add(expected.phase);
    codes.add(expected.code);
  }
  return { records: lines.length, phases: phases.size, codes: codes.size };
}

if (require.main === module) {
  try {
    const { values } = parseArgs({
      options: { input: { type: 'string' } },
      strict: true,
      allowPositionals: false,
    });
    const result = verifyUpdateHostDiagnostics(values.input ?? '');
    process.stdout.write(
      `Native update diagnostic contract passed: ${result.phases} phases, ${result.codes} codes, ${result.records} emitted records.\n`,
    );
  } catch (error) {
    process.stderr.write((error instanceof Error ? error.message : String(error)) + '\n');
    process.exitCode = 1;
  }
}

module.exports = { verifyUpdateHostDiagnostics };
