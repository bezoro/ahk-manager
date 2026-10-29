const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const esbuild = require('esbuild');
const output = process.argv[process.argv.indexOf('--out') + 1];
if (!process.argv.includes('--out') || !path.isAbsolute(output) || fs.existsSync(output))
  throw new Error('Supply a fresh absolute --out test staging directory');
fs.mkdirSync(output, { recursive: true });
const diagnosticsRoot = path.join(output, 'diagnostics-fixtures');
fs.mkdirSync(diagnosticsRoot);
const entries = ['main', 'renderer'].flatMap((scope) => {
  const folder = path.join('tests', scope);
  return fs.existsSync(folder)
    ? fs
        .readdirSync(folder)
        .filter((file) => file.endsWith('.test.ts') || file.endsWith('.test.tsx'))
        .map((file) => path.join(folder, file))
    : [];
});
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
  const result = spawnSync(process.execPath, ['--test', ...tests], {
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
