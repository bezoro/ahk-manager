import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  exportDiagnosticArchive,
  writeDiagnosticArchive,
} from '../../src/main/logging/diagnostic-export.js';

test('cancelled or stale save dialogs never collect or write diagnostic files', async () => {
  let builds = 0,
    writes = 0;
  const ports = {
    chooseDestination: async (): Promise<string | null> => null,
    alive: () => true,
    build: () => {
      builds++;
      return { bytes: Buffer.from('zip'), complete: true, manifest: { issues: [] } };
    },
    write: () => {
      writes++;
    },
  };
  assert.equal(await exportDiagnosticArchive(ports), null);
  assert.equal(builds, 0);
  assert.equal(writes, 0);
  ports.chooseDestination = async () => '/chosen.zip';
  ports.alive = () => false;
  await assert.rejects(exportDiagnosticArchive(ports), /document changed/i);
  assert.equal(builds, 0);
  assert.equal(writes, 0);
});

test('complete and partial archives report their collection result only after one successful native write', async () => {
  for (const complete of [true, false]) {
    const writes: string[] = [];
    const result = await exportDiagnosticArchive({
      chooseDestination: async () => '/chosen.zip',
      alive: () => true,
      build: () => ({
        bytes: Buffer.from('zip'),
        complete,
        manifest: { issues: complete ? [] : ['unreadable'] },
      }),
      write: (destination, bytes) => {
        assert.equal(bytes.toString(), 'zip');
        writes.push(destination);
      },
    });
    assert.deepEqual(result, { complete, issueCount: complete ? 0 : 1 });
    assert.deepEqual(writes, ['/chosen.zip']);
  }
  let writes = 0;
  await assert.rejects(
    exportDiagnosticArchive({
      chooseDestination: async () => '/chosen.zip',
      alive: () => true,
      build: () => ({ bytes: Buffer.from('zip'), complete: true, manifest: { issues: [] } }),
      write: () => {
        writes++;
        throw new Error('disk full');
      },
    }),
    /disk full/,
  );
  assert.equal(writes, 1);
});

test('a document invalidated during collection cannot save the archive', async () => {
  let alive = true,
    writes = 0;
  await assert.rejects(
    exportDiagnosticArchive({
      chooseDestination: async () => '/chosen.zip',
      alive: () => alive,
      build: () => {
        alive = false;
        return { bytes: Buffer.from('zip'), complete: true, manifest: { issues: [] } };
      },
      write: () => {
        writes++;
      },
    }),
    /document changed/i,
  );
  assert.equal(writes, 0);
});

test('archive writes stage and replace only the selected regular output, preserving unrelated files', (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-archive-save-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const destination = path.join(directory, 'report.zip');
  const unrelated = path.join(directory, 'unrelated.txt');
  fs.writeFileSync(unrelated, 'preserve');
  writeDiagnosticArchive(destination, Buffer.from('complete archive'));
  assert.equal(fs.readFileSync(destination, 'utf8'), 'complete archive');
  writeDiagnosticArchive(destination, Buffer.from('new archive'));
  assert.equal(fs.readFileSync(destination, 'utf8'), 'new archive');
  assert.equal(fs.readFileSync(unrelated, 'utf8'), 'preserve');
  assert.deepEqual(fs.readdirSync(directory).sort(), ['report.zip', 'unrelated.txt']);
});

test('linked destinations are rejected without changing the target bytes', (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-archive-links-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const original = path.join(directory, 'original.zip');
  const linked = path.join(directory, 'linked.zip');
  fs.writeFileSync(original, 'preserve');
  fs.linkSync(original, linked);
  assert.throws(() => writeDiagnosticArchive(linked, Buffer.from('unsafe')), /could not be saved/);
  assert.equal(fs.readFileSync(original, 'utf8'), 'preserve');
  fs.unlinkSync(linked);
  if (process.platform !== 'win32') {
    fs.symlinkSync(original, linked);
    assert.throws(
      () => writeDiagnosticArchive(linked, Buffer.from('unsafe')),
      /could not be saved/,
    );
    assert.equal(fs.readFileSync(original, 'utf8'), 'preserve');
  }
});

test('failed publication preserves the previous archive and cleans only its own staged file', (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-archive-publish-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const destination = path.join(directory, 'report.zip');
  fs.writeFileSync(destination, 'previous');
  const originalRename = fs.renameSync;
  const rename = t.mock.method(fs, 'renameSync', (source: fs.PathLike, target: fs.PathLike) => {
    if (String(target) === destination)
      throw Object.assign(new Error('private path'), { code: 'EACCES' });
    return originalRename(source, target);
  });
  assert.throws(
    () => writeDiagnosticArchive(destination, Buffer.from('new')),
    /could not be saved/,
  );
  rename.mock.restore();
  assert.equal(fs.readFileSync(destination, 'utf8'), 'previous');
  assert.deepEqual(fs.readdirSync(directory), ['report.zip']);
});
