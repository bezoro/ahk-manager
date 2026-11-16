const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const esbuild = require('esbuild');
const { testInputs, discoverTests } = require('./lib/test-inputs.cjs');
const { output, namePattern } = testInputs(process.argv.slice(2));
fs.mkdirSync(output, { recursive: true });
const diagnosticsRoot = path.join(output, 'diagnostics-fixtures');
fs.mkdirSync(diagnosticsRoot);
const entries = discoverTests('tests');
if (!entries.length) throw new Error('No test entries');
(async () => {
  await esbuild.build({
    entryPoints: entries,
    outdir: output,
    outbase: 'tests',
    bundle: true,
    platform: 'node',
    format: 'cjs',
    target: 'node24',
    external: ['electron'],
    logLevel: 'info',
  });
  const tests = entries.map((file) =>
    path.join(output, path.relative('tests', file).replace(/\.tsx?$/, '.js')),
  );
  const filter = namePattern === undefined ? [] : ['--test-name-pattern', namePattern];
  const result = spawnSync(process.execPath, ['--test', ...filter, ...tests], {
    stdio: 'inherit',
    shell: false,
    env: { ...process.env, AHK_DIAGNOSTICS_TEST_ROOT: diagnosticsRoot },
  });
  if (result.error) throw result.error;
  process.exitCode = result.status ?? 1;
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
