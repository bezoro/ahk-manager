const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { dotnetProjects } = require('./lib/dotnet-projects.cjs');

const repositoryRoot = path.resolve(__dirname, '../..');
const checking = process.argv.includes('--check');
const requested = process.argv.slice(2).filter((argument) => argument !== '--check');
const defaultProjects = dotnetProjects.map((project) => project.directory);
const projects = new Set(defaultProjects);

if (requested.some((project) => !projects.has(project))) {
  throw new Error(`Use --check and optional project names: ${[...projects].join(', ')}.`);
}

const selected = requested.length ? requested : defaultProjects;
console.log(`${checking ? 'Checking' : 'Formatting'} ${selected.join(', ')} with CSharpier.`);
const result = spawnSync(
  'dotnet',
  ['tool', 'run', 'csharpier', '--', checking ? 'check' : 'format', ...selected],
  { cwd: repositoryRoot, stdio: 'inherit', shell: false },
);
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
