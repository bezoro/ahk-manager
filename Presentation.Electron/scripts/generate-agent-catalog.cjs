const fs = require('node:fs/promises');
const path = require('node:path');
const esbuild = require('esbuild');
const prettier = require('prettier');

async function main() {
  const shared = path.join(__dirname, '../src/shared');
  const catalogFile = path.join(shared, 'agent-commands.json');
  const bundle = await esbuild.build({
    entryPoints: [path.join(shared, 'agent-command-catalog.ts')],
    bundle: true,
    format: 'esm',
    platform: 'node',
    write: false,
  });
  const { describeAgentCommands } = await import(
    'data:text/javascript;base64,' + Buffer.from(bundle.outputFiles[0].contents).toString('base64')
  );
  const catalog = JSON.parse(await fs.readFile(catalogFile, 'utf8'));
  const output = await prettier.format(JSON.stringify(describeAgentCommands(catalog)), {
    ...(await prettier.resolveConfig(catalogFile)),
    filepath: catalogFile,
  });
  await fs.writeFile(catalogFile, output);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
