const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { git, resolveCommit, latestAncestorTag } = require('./compute-release.cjs');

const repositoryRoot = path.resolve(__dirname, '../..');
const config = path.resolve(__dirname, '../commitlint.config.cjs');

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

async function lintRange(cwd, range) {
  // The pinned CLI silently ignores --git-log-args=--no-merges; select commits in Git.
  git(cwd, ['merge-base', range.from, range.to]);
  const output = execFileSync(
    'git',
    [
      'log',
      '-z',
      '--no-merges',
      '--format=%B',
      '--end-of-options',
      `${range.from}..${range.to}`,
      '--',
    ],
    { cwd, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 },
  );
  const messages = output ? output.split('\0').slice(0, -1) : [];
  const [load, lint, format] = await Promise.all(
    ['load', 'lint', 'format'].map(async (name) => {
      const module = await import(`@commitlint/${name}`);
      return module.default;
    }),
  );
  const loaded = await load({}, { cwd, file: config });
  if (!Object.keys(loaded.rules).length) throw new Error('Commitlint rules must not be empty.');
  const results = await Promise.all(
    messages.map((message) =>
      lint(message, loaded.rules, {
        parserOpts: loaded.parserPreset?.parserOpts,
        plugins: loaded.plugins,
        ignores: loaded.ignores,
        defaultIgnores: loaded.defaultIgnores,
      }),
    ),
  );
  return {
    status: results.every((result) => result.valid) ? 0 : 1,
    stdout: format({ results }, { color: false, verbose: true, helpUrl: loaded.helpUrl }),
    stderr: '',
  };
}

if (require.main === module) {
  (async () => {
    const event = process.env.GITHUB_EVENT_PATH
      ? JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
      : {};
    const result = await lintRange(
      repositoryRoot,
      commitRange(repositoryRoot, event, process.env.GITHUB_SHA || 'HEAD'),
    );
    if (result.stdout) process.stdout.write(result.stdout);
    if (result.stderr) process.stderr.write(result.stderr);
    process.exitCode = result.status;
  })().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

module.exports = { commitRange, lintRange };
