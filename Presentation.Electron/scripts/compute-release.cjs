const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { parseArgs } = require('node:util');
const semver = require('semver');
const removeMarkdown = require('remove-markdown');
const { commitTypes } = require('./release-policy.cjs');

const repositoryRoot = path.resolve(__dirname, '../..');

function git(cwd, args) {
  return execFileSync('git', args, {
    cwd,
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
  }).trimEnd();
}

function resolveCommit(cwd, revision) {
  if (!/^(?:HEAD|[0-9a-f]{40})$/i.test(revision))
    throw new Error('Use HEAD or a complete commit SHA.');
  return git(cwd, ['rev-parse', '--verify', `${revision}^{commit}`]);
}

function latestStableTag(cwd, sha) {
  const tags = git(cwd, sha ? ['tag', '--merged', sha] : ['tag'])
    .split('\n')
    .filter(
      (tag) =>
        /^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/.test(tag) && semver.valid(tag.slice(1)),
    );
  tags.sort((a, b) => semver.rcompare(a.slice(1), b.slice(1)));
  if (!tags.length)
    throw new Error('No ancestral stable SemVer tag found. Fetch complete history and tags.');
  return {
    tag: tags[0],
    version: tags[0].slice(1),
    commit: git(cwd, ['rev-list', '-n', '1', tags[0]]),
  };
}

const latestAncestorTag = (cwd, sha) => latestStableTag(cwd, sha);

function testIdentifiers(test) {
  if (!test) return null;
  const parts = [test.run, test.attempt, test.pr].map(String);
  if (
    !parts.every(
      (part) =>
        /^[1-9]\d*$/.test(part) && String(Number(part)) === part && Number(part) <= 2147483647,
    )
  )
    throw new Error(
      'Test release identifiers must be positive integers no greater than 2147483647.',
    );
  return parts.join('.');
}

function commitsSince(cwd, from, to) {
  const output = git(cwd, ['log', '-z', '--format=%H%x00%B', `${from}..${to}`, '--']);
  if (!output) return [];
  const fields = output.split('\0');
  const commits = [];
  for (let index = 0; index + 1 < fields.length; index += 2)
    commits.push({ hash: fields[index], message: fields[index + 1] });
  return commits;
}

function plainNotes(markdown) {
  const text = removeMarkdown(markdown, { useLinkText: true, stripListLeaders: false })
    .replace(/\r\n/g, '\n')
    .trim();
  if (text.length <= 4000) return text;
  const suffix = '\n\nNotes shortened to fit the update history.';
  let clipped = text.slice(0, 4000 - suffix.length);
  if (/[\uD800-\uDBFF]$/.test(clipped)) clipped = clipped.slice(0, -1);
  return clipped.trimEnd() + suffix;
}

async function computeRelease({ cwd = repositoryRoot, sha = 'HEAD', test } = {}) {
  const preview = testIdentifiers(test);
  const commit = resolveCommit(cwd, sha);
  const base = preview ? latestStableTag(cwd) : latestAncestorTag(cwd, commit);
  // A shared feature commit can already be released through a merge on main
  // without that merge being an ancestor of the PR. Count only changes since
  // their common ancestor so its feature bump is not applied a second time.
  const from = preview ? git(cwd, ['merge-base', base.commit, commit]) : base.tag;
  const commits = commitsSince(cwd, from, commit);
  const [{ default: createPreset }, { CommitParser }, { writeChangelogString }] = await Promise.all(
    [
      import('conventional-changelog-conventionalcommits'),
      import('conventional-commits-parser'),
      import('conventional-changelog-writer'),
    ],
  );
  const preset = createPreset({ types: commitTypes });
  const parser = new CommitParser(preset.parser);
  const parsed = commits.map(({ hash, message }) => ({ ...parser.parse(message), hash }));
  const bump = preset.whatBump(parsed);
  const alreadyTagged = !preview && base.commit === commit;
  const releaseType = alreadyTagged ? null : ['major', 'minor', 'patch'][bump?.level ?? 2];
  // A PR can lag behind main. Its preview must still sort after the currently
  // published stable baseline without claiming that stable tag for itself.
  const versionBase = base.version;
  const stableVersion = alreadyTagged ? base.version : semver.inc(versionBase, releaseType);
  const version = preview ? `${stableVersion}-test.${preview}` : stableVersion;
  if (!version) throw new Error('The next stable SemVer version could not be calculated.');
  if (
    !semver.valid(version) ||
    version.length > 64 ||
    [semver.major(version), semver.minor(version), semver.patch(version)].some(
      (part) => part > 2147483647,
    )
  )
    throw new Error('The release version exceeds the supported Windows update identity.');
  const releaseTag = `v${version}`;
  if (
    git(cwd, ['tag', '--list', releaseTag]) &&
    git(cwd, ['rev-parse', '--verify', `refs/tags/${releaseTag}^{commit}`]) !== commit
  ) {
    throw new Error(`Release tag ${releaseTag} already exists for a different commit.`);
  }
  const markdown = await writeChangelogString(
    parsed,
    {
      version,
      host: 'https://github.com',
      owner: 'bezoro',
      repository: 'ahk-manager',
      previousTag: base.tag,
      currentTag: `v${version}`,
      linkCompare: false,
      linkReferences: false,
    },
    preset.writer,
  );
  return {
    version,
    baseVersion: versionBase,
    baseTag: base.tag,
    commit,
    releaseType,
    alreadyTagged,
    commitCount: commits.length,
    notes: plainNotes(markdown),
  };
}

if (require.main === module) {
  const { values } = parseArgs({
    options: {
      json: { type: 'boolean', default: false },
      sha: { type: 'string' },
      repo: { type: 'string' },
      'test-pr': { type: 'string' },
      'test-run': { type: 'string' },
      'test-attempt': { type: 'string' },
    },
  });
  computeRelease({
    cwd: values.repo ? path.resolve(values.repo) : repositoryRoot,
    sha: values.sha || process.env.GITHUB_SHA || 'HEAD',
    test: ['test-pr', 'test-run', 'test-attempt'].some((key) => values[key] !== undefined)
      ? { pr: values['test-pr'], run: values['test-run'], attempt: values['test-attempt'] }
      : undefined,
  })
    .then((release) => {
      if (process.env.GITHUB_OUTPUT && !values.json) {
        fs.appendFileSync(
          process.env.GITHUB_OUTPUT,
          [
            `version=${release.version}`,
            `base-tag=${release.baseTag}`,
            `already-tagged=${release.alreadyTagged}`,
            `notes-base64=${Buffer.from(release.notes, 'utf8').toString('base64')}`,
            '',
          ].join('\n'),
        );
      }
      if (values.json) console.log(JSON.stringify(release));
      else
        console.log(
          `Release ${release.version}: ${release.commitCount} commits since ${release.baseTag}${release.alreadyTagged ? ', existing tag reused' : `, ${release.releaseType} bump`}.`,
        );
    })
    .catch((error) => {
      console.error(error.message);
      process.exitCode = 1;
    });
}

module.exports = { computeRelease, latestAncestorTag, resolveCommit, git, plainNotes };
