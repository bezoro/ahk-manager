const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { computeRelease, git, plainNotes } = require('../../scripts/compute-release.cjs');
const { commitRange, lintRange } = require('../../scripts/check-commits.cjs');
const { commitTypes } = require('../../scripts/release-policy.cjs');

test('test builds use chronological run, attempt and PR identifiers without reserving the stable tag', async (t) => {
  const fixture = repository(t);
  fixture.commit('feat: add test channel');
  const first = await computeRelease({ ...fixture, test: { pr: 42, run: 19, attempt: 1 } });
  const retried = await computeRelease({ ...fixture, test: { pr: 42, run: 19, attempt: 2 } });
  const next = await computeRelease({ ...fixture, test: { pr: 2, run: 20, attempt: 1 } });
  assert.equal(first.version, '1.3.0-test.19.1.42');
  assert.equal(retried.version, '1.3.0-test.19.2.42');
  assert.equal(next.version, '1.3.0-test.20.1.2');
  assert.equal(require('semver').lt(first.version, retried.version), true);
  assert.equal(require('semver').lt(retried.version, next.version), true);
  assert.equal(git(fixture.cwd, ['tag', '--list', 'v1.3.0*']), '');
});

test('a PR behind main bases its preview on the highest stable tag, without claiming that stable identity', async (t) => {
  const fixture = repository(t);
  git(fixture.cwd, ['checkout', '--quiet', '-b', 'feature']);
  const head = fixture.commit('fix: preview an isolated change');
  git(fixture.cwd, ['checkout', '--quiet', 'main']);
  fixture.commit('feat: ship bootstrap');
  git(fixture.cwd, ['tag', 'v1.3.0']);
  const release = await computeRelease({
    ...fixture,
    sha: head,
    test: { pr: 2, run: 20, attempt: 1 },
  });
  assert.equal(release.version, '1.3.1-test.20.1.2');
  assert.equal(release.baseVersion, '1.3.0');
  assert.equal(release.commit, head);
  assert.equal(release.alreadyTagged, false);
});

test('a conflicting next stable tag does not block an independent test release', async (t) => {
  const fixture = repository(t);
  git(fixture.cwd, ['checkout', '--quiet', '-b', 'unrelated']);
  fixture.commit('fix: reserve stable patch');
  git(fixture.cwd, ['tag', 'v1.2.17']);
  git(fixture.cwd, ['checkout', '--quiet', 'main']);
  fixture.commit('fix: preview the branch');
  const release = await computeRelease({ ...fixture, test: { pr: 2, run: 20, attempt: 1 } });
  assert.equal(release.version, '1.2.18-test.20.1.2');
});

test('a shared feature released through main is excluded from the remaining PR preview bump', async (t) => {
  const fixture = repository(t);
  git(fixture.cwd, ['checkout', '--quiet', '-b', 'bootstrap']);
  fixture.commit('feat: enable the shared bootstrap channel');
  git(fixture.cwd, ['checkout', '--quiet', '-b', 'remaining-pr']);
  const head = fixture.commit('refactor: organize application responsibilities');
  git(fixture.cwd, ['checkout', '--quiet', 'main']);
  git(fixture.cwd, ['merge', '--quiet', '--no-ff', 'bootstrap', '-m', 'Merge bootstrap PR']);
  git(fixture.cwd, ['tag', 'v1.3.0']);
  const preview = await computeRelease({
    ...fixture,
    sha: head,
    test: { pr: 2, run: 21, attempt: 1 },
  });
  assert.equal(preview.version, '1.3.1-test.21.1.2');
  assert.equal(preview.baseTag, 'v1.3.0');
  assert.equal(preview.releaseType, 'patch');
  assert.equal(preview.commitCount, 1);
  assert.doesNotMatch(preview.notes, /shared bootstrap channel/);
  git(fixture.cwd, ['merge', '--quiet', '--no-ff', 'remaining-pr', '-m', 'Merge remaining PR']);
  const stable = await computeRelease(fixture);
  assert.equal(stable.version, preview.version.split('-')[0]);
});

test('test identity refuses partial, unbounded and noncanonical CI identifiers', async (t) => {
  const fixture = repository(t);
  fixture.commit('fix: preview');
  for (const test of [
    { pr: 2, run: 20 },
    { pr: 0, run: 20, attempt: 1 },
    { pr: 2, run: '01', attempt: 1 },
    { pr: 2, run: 2147483648, attempt: 1 },
    { pr: 2, run: 20, attempt: '1\n' },
  ])
    await assert.rejects(computeRelease({ ...fixture, test }), /positive integers/);
});

function repository(t) {
  const cwd = fs.mkdtempSync(path.join(process.env.RUNNER_TEMP || os.tmpdir(), 'ahk-release-'));
  t.after(() => fs.rmSync(cwd, { recursive: true, force: true }));
  git(cwd, ['init', '--quiet', '--initial-branch=main']);
  git(cwd, ['config', 'user.name', 'Release fixture']);
  git(cwd, ['config', 'user.email', 'release-fixture@example.invalid']);
  git(cwd, ['config', 'commit.gpgsign', 'false']);
  git(cwd, ['config', 'tag.gpgsign', 'false']);
  const hooks = path.join(cwd, 'empty-hooks');
  fs.mkdirSync(hooks);
  git(cwd, ['config', 'core.hooksPath', hooks]);
  const commit = (message) => {
    git(cwd, ['commit', '--quiet', '--allow-empty', '--no-verify', '-m', message]);
    return git(cwd, ['rev-parse', 'HEAD']);
  };
  const base = commit('Legacy message before adopting conventions');
  git(cwd, ['tag', 'v1.2.16']);
  return { cwd, commit, base };
}

for (const { type } of commitTypes) {
  test(`${type} produces the agreed SemVer bump and is accepted by commitlint`, async (t) => {
    const fixture = repository(t);
    fixture.commit(`${type}: improve fixture behavior`);
    const release = await computeRelease(fixture);
    assert.equal(release.version, type === 'feat' ? '1.3.0' : '1.2.17');
    assert.equal(release.releaseType, type === 'feat' ? 'minor' : 'patch');
    assert.equal(release.commitCount, 1);
    assert.equal(release.alreadyTagged, false);
    assert.match(release.notes, /improve fixture behavior/);
    const lint = lintRange(fixture.cwd, { from: fixture.base, to: release.commit });
    assert.equal(lint.status, 0, lint.stdout + lint.stderr);
  });
}

for (const message of [
  'feat!: remove the old profile schema',
  'fix(storage)!: remove the old profile schema',
  'refactor: remove the old profile schema\n\nBREAKING CHANGE: old profiles require migration.',
]) {
  test(`breaking change takes priority: ${message.split('\n')[0]}`, async (t) => {
    const fixture = repository(t);
    fixture.commit(message);
    const release = await computeRelease(fixture);
    assert.equal(release.version, '2.0.0');
    assert.equal(release.releaseType, 'major');
    assert.match(release.notes, /old profile/);
    const lint = lintRange(fixture.cwd, { from: fixture.base, to: release.commit });
    assert.equal(lint.status, 0, lint.stdout + lint.stderr);
  });
}

test('mixed commits choose the highest bump, using numeric reachable tags and ignoring unrelated tags', async (t) => {
  const fixture = repository(t);
  git(fixture.cwd, ['tag', 'v1.9.0']);
  git(fixture.cwd, ['tag', 'v1.10.0']);
  git(fixture.cwd, ['checkout', '--quiet', '-b', 'unrelated']);
  fixture.commit('feat!: unrelated breaking change');
  git(fixture.cwd, ['tag', 'v99.0.0']);
  git(fixture.cwd, ['checkout', '--quiet', 'main']);
  fixture.commit('fix: correct startup');
  fixture.commit('feat: add script grouping');
  fixture.commit('docs: explain startup');
  const release = await computeRelease(fixture);
  assert.equal(release.baseTag, 'v1.10.0');
  assert.equal(release.version, '1.11.0');
  assert.equal(release.commitCount, 3);
});

test('rerunning a tagged commit reuses the tag version without inventing another patch', async (t) => {
  const fixture = repository(t);
  fixture.commit('fix: recover startup');
  git(fixture.cwd, ['tag', 'v1.2.17']);
  const release = await computeRelease(fixture);
  assert.equal(release.version, '1.2.17');
  assert.equal(release.baseTag, 'v1.2.17');
  assert.equal(release.alreadyTagged, true);
  assert.equal(release.releaseType, null);
  assert.equal(release.commitCount, 0);
});

for (const annotated of [false, true]) {
  test(`a calculated version refuses a conflicting ${annotated ? 'annotated' : 'lightweight'} tag outside its ancestry`, async (t) => {
    const fixture = repository(t);
    git(fixture.cwd, ['checkout', '--quiet', '-b', 'unrelated']);
    const other = fixture.commit('fix: reserve an unrelated version');
    git(
      fixture.cwd,
      annotated
        ? ['tag', '-a', 'v1.2.17', '-m', 'Unrelated annotated release']
        : ['tag', 'v1.2.17'],
    );
    git(fixture.cwd, ['checkout', '--quiet', 'main']);
    fixture.commit('fix: release the main branch');
    await assert.rejects(
      computeRelease(fixture),
      /v1\.2\.17 already exists for a different commit/,
    );
    assert.equal(git(fixture.cwd, ['rev-parse', 'refs/tags/v1.2.17^{commit}']), other);
  });
}

test('an annotated tag on the requested commit is reused without moving its reference', async (t) => {
  const fixture = repository(t);
  const head = fixture.commit('fix: publish the annotated version');
  git(fixture.cwd, ['tag', '-a', 'v1.2.17', '-m', 'Owned annotated release']);
  const tagObject = git(fixture.cwd, ['rev-parse', 'refs/tags/v1.2.17']);
  const release = await computeRelease(fixture);
  assert.equal(release.version, '1.2.17');
  assert.equal(release.commit, head);
  assert.equal(release.alreadyTagged, true);
  assert.equal(git(fixture.cwd, ['rev-parse', 'refs/tags/v1.2.17']), tagObject);
});

test('new PR range excludes old baseline and target-branch changes after divergence', (t) => {
  const fixture = repository(t);
  git(fixture.cwd, ['checkout', '--quiet', '-b', 'feature']);
  const head = fixture.commit('feat: expose release history');
  git(fixture.cwd, ['checkout', '--quiet', 'main']);
  const base = fixture.commit('Old target branch commit');
  const range = commitRange(fixture.cwd, {
    pull_request: { base: { sha: base }, head: { sha: head } },
  });
  assert.deepEqual(range, { from: fixture.base, to: head });
  const lint = lintRange(fixture.cwd, range);
  assert.equal(lint.status, 0, lint.stdout + lint.stderr);
});

test('push checks only its new commits; invalid subjects fail and generated merge subjects retain default ignores', (t) => {
  const fixture = repository(t);
  const invalid = fixture.commit('Improve code without a conventional type');
  let range = commitRange(fixture.cwd, { before: fixture.base }, invalid);
  assert.notEqual(lintRange(fixture.cwd, range).status, 0);
  const merge = fixture.commit(
    'Merge pull request #42 from fixture/topic\n\nfix: improve release identity',
  );
  range = commitRange(fixture.cwd, { before: invalid }, merge);
  const lint = lintRange(fixture.cwd, range);
  assert.equal(lint.status, 0, lint.stdout + lint.stderr);
});

test('manifest notes are plain text with readable code/link labels and preserve the 4000 UTF-16 limit', async (t) => {
  const fixture = repository(t);
  fixture.commit(
    'fix: render `code` and [label](https://user:fixture-secret@example.invalid/path)',
  );
  const release = await computeRelease(fixture);
  assert.match(release.notes, /render code and label/);
  assert.doesNotMatch(release.notes, /https?:|fixture-secret|`|\]\(|^#+ /m);
  const clipped = plainNotes('## Long release\n\n' + 'á🙂'.repeat(3000));
  assert.ok(clipped.length <= 4000);
  assert.match(clipped, /Notes shortened/);
  assert.doesNotMatch(clipped, /[\uD800-\uDBFF](?![\uDC00-\uDFFF])/u);
  for (const prefix of ['', 'x']) {
    const boundary = plainNotes(prefix + '🙂'.repeat(3000));
    assert.ok(boundary.length <= 4000);
    assert.doesNotMatch(boundary, /[\uD800-\uDBFF](?![\uDC00-\uDFFF])/u);
  }
});

test('missing stable ancestor tag refuses to guess a release identity', async (t) => {
  const fixture = repository(t);
  git(fixture.cwd, ['tag', '-d', 'v1.2.16']);
  await assert.rejects(computeRelease(fixture), /No ancestral stable SemVer tag/);
});

test('local build CLI emits only the requested commit identity and does not write workflow outputs', (t) => {
  const fixture = repository(t);
  const sha = fixture.commit('fix: identify local builds from Git tags');
  const output = path.join(fixture.cwd, 'workflow-output');
  const cli = path.resolve(__dirname, '../../scripts/compute-release.cjs');
  const result = spawnSync(process.execPath, [cli, '--repo', fixture.cwd, '--sha', sha, '--json'], {
    encoding: 'utf8',
    env: { ...process.env, GITHUB_OUTPUT: output },
  });
  assert.equal(result.status, 0, result.stderr);
  const identity = JSON.parse(result.stdout);
  assert.equal(identity.version, '1.2.17');
  assert.equal(identity.commit, sha);
  assert.equal(fs.existsSync(output), false);
});

test('a retried Windows build can calculate its own current attempt without cached job outputs', (t) => {
  const fixture = repository(t);
  const sha = fixture.commit('feat: publish a preview for this build attempt');
  const cli = path.resolve(__dirname, '../../scripts/compute-release.cjs');
  const output = path.join(fixture.cwd, 'workflow-output');
  for (const attempt of [1, 2]) {
    const args = [
      cli,
      '--repo',
      fixture.cwd,
      '--json',
      '--sha',
      sha,
      '--test-pr',
      '3',
      '--test-run',
      '22',
      '--test-attempt',
      String(attempt),
    ];
    const result = spawnSync(process.execPath, args, {
      encoding: 'utf8',
      env: { ...process.env, GITHUB_OUTPUT: output },
    });
    assert.equal(result.status, 0, result.stderr);
    const identity = JSON.parse(result.stdout);
    assert.equal(identity.version, `1.3.0-test.22.${attempt}.3`);
    assert.equal(identity.commit, sha);
    assert.match(identity.notes, new RegExp(`1\\.3\\.0-test\\.22\\.${attempt}\\.3`));
    assert.equal(fs.existsSync(output), false);
  }
});
