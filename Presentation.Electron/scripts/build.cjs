const fs = require('node:fs');
const path = require('node:path');
const esbuild = require('esbuild');
const { writeApplicationIcons } = require('./lib/application-icons.cjs');
const { writeDependencyLicenses } = require('./lib/dependency-licenses.cjs');
const output = process.argv[process.argv.indexOf('--out') + 1];
if (!process.argv.includes('--out') || !path.isAbsolute(output))
  throw new Error('Supply an absolute --out staging directory');
if (fs.existsSync(output))
  throw new Error('Use a fresh output directory; previous build evidence must remain intact');
fs.mkdirSync(output, { recursive: true });
const renderer = path.join(output, 'renderer');
fs.mkdirSync(renderer);
const common = {
  bundle: true,
  sourcemap: false,
  logLevel: 'info',
  target: 'es2023',
  legalComments: 'external',
};
(async () => {
  await esbuild.build({
    ...common,
    entryPoints: ['src/main/index.ts'],
    outfile: path.join(output, 'main/index.cjs'),
    platform: 'node',
    format: 'cjs',
    external: ['electron'],
  });
  await esbuild.build({
    ...common,
    entryPoints: ['src/preload/index.ts'],
    outfile: path.join(output, 'preload/index.cjs'),
    platform: 'node',
    format: 'cjs',
    external: ['electron'],
  });
  await esbuild.build({
    ...common,
    entryPoints: ['src/renderer/main.tsx'],
    outfile: path.join(renderer, 'app.js'),
    platform: 'browser',
    format: 'iife',
    minify: true,
    define: { 'process.env.NODE_ENV': '"production"' },
  });
  fs.copyFileSync('src/renderer/index.html', path.join(renderer, 'index.html'));
  if (!fs.existsSync(path.join(renderer, 'app.css')))
    fs.copyFileSync('src/renderer/styles.css', path.join(renderer, 'app.css'));
  const assets = fs
    .readdirSync(renderer)
    .filter((name) => /\.(html|js|css|svg|png|woff2)$/.test(name));
  fs.writeFileSync(path.join(output, 'assets.json'), JSON.stringify(assets));
  writeApplicationIcons(output);
  fs.writeFileSync(
    path.join(output, 'build-info.json'),
    JSON.stringify(
      {
        versions: process.versions,
        build: 'production',
        sourceMaps: false,
        rendererAssets: assets,
      },
      null,
      2,
    ),
  );
  writeDependencyLicenses(output);
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
