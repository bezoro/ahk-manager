const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const esbuild = require('esbuild');

const argument = process.argv.indexOf('--out');
const output = process.argv[argument + 1];
if (argument < 0 || !output || !path.isAbsolute(output) || fs.existsSync(output))
  throw new Error('Supply a fresh absolute --out network harness directory.');
const legacyCommit = 'ff035abbf8fb53348a38cbd60e36287a82f5ec6f';
const legacySource = execFileSync(
  'git',
  ['show', `${legacyCommit}:Presentation.Electron/src/main/github-updates.ts`],
  { cwd: path.resolve(__dirname, '../..'), encoding: 'utf8', maxBuffer: 1024 * 1024 },
);
fs.mkdirSync(output, { recursive: true });
(async () => {
  await esbuild.build({
    stdin: { contents: legacySource, loader: 'ts', sourcefile: 'legacy-1.2.16-client.ts' },
    outfile: path.join(output, 'legacy-client.cjs'),
    bundle: true,
    platform: 'node',
    format: 'cjs',
    target: 'node24',
  });
  await esbuild.build({
    entryPoints: [path.resolve(__dirname, '../tests/main/update-network-harness.ts')],
    outfile: path.join(output, 'update-network-harness.cjs'),
    bundle: true,
    platform: 'node',
    format: 'cjs',
    target: 'node24',
    external: ['electron'],
  });
})().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
