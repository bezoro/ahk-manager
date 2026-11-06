const fs = require('node:fs');
const path = require('node:path');
const esbuild = require('esbuild');

const argument = process.argv.indexOf('--out');
const output = process.argv[argument + 1];
if (argument < 0 || !output || !path.isAbsolute(output) || fs.existsSync(output))
  throw new Error('Supply a fresh absolute --out network harness directory.');
fs.mkdirSync(output, { recursive: true });
(async () => {
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
