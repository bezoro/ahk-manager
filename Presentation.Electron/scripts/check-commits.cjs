const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { git, resolveCommit, latestAncestorTag } = require('./compute-release.cjs');

const repositoryRoot = path.resolve(__dirname, '../..');
const config = path.resolve(__dirname, '../commitlint.config.cjs');
const cli = path.join(path.dirname(require.resolve('@commitlint/cli')), 'cli.js');

function commitRange(cwd, event, sha = 'HEAD') {
  const to = resolveCommit(cwd, event.pull_request?.head.sha || sha);
  let from;
  if (event.pull_request) {
    const base = resolveCommit(cwd, event.pull_request.base.sha);
    from = git(cwd, ['merge-base', base, to]);
  } else if (event.before && !/^0+$/.test(event.before)) {
    from = resolveCommit(cwd, event.before);
  } else from = latestAncestorTag(cwd, to).commit;
  return { from, to };
}

function lintRange(cwd, range) {
  return spawnSync(
    process.execPath,
    [cli, '--config', config, '--from', range.from, '--to', range.to, '--verbose'],
    { cwd, encoding: 'utf8', shell: false },
  );
}

if (require.main === module) {
  try {
    const event = process.env.GITHUB_EVENT_PATH
      ? JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
      : {};
    const result = lintRange(
      repositoryRoot,
      commitRange(repositoryRoot, event, process.env.GITHUB_SHA || 'HEAD'),
    );
    if (result.stdout) process.stdout.write(result.stdout);
    if (result.stderr) process.stderr.write(result.stderr);
    if (result.error) throw result.error;
    process.exitCode = result.status ?? 1;
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

module.exports = { commitRange, lintRange };
