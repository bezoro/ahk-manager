const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { parseArgs } = require('node:util');
const { validateRelative } = require('./owned-files.cjs');
const { validUpdateVersion } = require('./lib/update-versions.cjs');

const APPLICATION_ID = 'Bezoro.AhkManager.Electron';
const LIMITS = Object.freeze({
  files: 10000,
  fileBytes: 512 * 1024 * 1024,
  totalBytes: 2 * 1024 * 1024 * 1024,
  descriptorBytes: 4 * 1024 * 1024,
});

function regular(file) {
  const info = fs.lstatSync(file);
  if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1 || info.size > LIMITS.fileBytes)
    throw new Error('Portable packages require bounded regular, single-link files.');
  return info;
}

function directory(folder) {
  for (let current = path.resolve(folder); ; current = path.dirname(current)) {
    const info = fs.lstatSync(current);
    if (!info.isDirectory() || info.isSymbolicLink())
      throw new Error('Portable package paths cannot be redirected.');
    if (path.dirname(current) === current) return;
  }
}

function stagePortablePackage({ sourceRoot, launcherPath, outputRoot, version, commit }) {
  if (!validUpdateVersion(version) || !/^[a-f0-9]{40}$/.test(commit))
    throw new Error('A portable package requires its exact release version and commit.');
  for (const value of [sourceRoot, launcherPath, outputRoot])
    if (!path.isAbsolute(value)) throw new Error('Use absolute portable packaging paths.');
  if (fs.existsSync(outputRoot)) throw new Error('Use a fresh portable output directory.');
  const relativeOutput = path.relative(sourceRoot, outputRoot);
  if (
    !relativeOutput ||
    (!relativeOutput.startsWith(`..${path.sep}`) &&
      relativeOutput !== '..' &&
      !path.isAbsolute(relativeOutput))
  )
    throw new Error('Portable output must stay outside the source application.');
  directory(sourceRoot);
  directory(path.dirname(launcherPath));
  directory(path.dirname(outputRoot));
  if (regular(launcherPath).size === 0) throw new Error('The portable launcher is empty.');
  const candidates = [{ source: launcherPath, relative: 'Bezoro.AhkManager.exe' }];
  const walk = (folder) => {
    const entries = fs
      .readdirSync(folder, { withFileTypes: true })
      .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    for (const entry of entries) {
      const source = path.join(folder, entry.name);
      const relative = validateRelative(path.relative(sourceRoot, source));
      if (relative.split('/')[0].toLowerCase() === 'data')
        throw new Error('User Data cannot be included in a portable application package.');
      const info = fs.lstatSync(source);
      if (info.isSymbolicLink()) throw new Error('Portable package entries cannot be redirected.');
      if (info.isDirectory()) walk(source);
      else {
        regular(source);
        candidates.push({ source, relative: `App/${relative}` });
      }
      if (candidates.length > LIMITS.files)
        throw new Error('Portable package file count exceeds its limit.');
    }
  };
  walk(sourceRoot);
  candidates.sort((a, b) => (a.relative < b.relative ? -1 : a.relative > b.relative ? 1 : 0));
  const seen = new Set();
  const directorySpellings = new Map();
  let bytes = 0;
  for (const file of candidates) {
    const key = file.relative.toLowerCase();
    if (seen.has(key) || file.relative.length > 1024)
      throw new Error('Portable package paths are duplicate or too long.');
    const parts = file.relative.split('/');
    for (let index = 1; index < parts.length; index++) {
      const ancestor = parts.slice(0, index).join('/');
      const previous = directorySpellings.get(ancestor.toLowerCase());
      if (seen.has(ancestor.toLowerCase()) || (previous && previous !== ancestor))
        throw new Error(
          'Portable package paths collide with a file or differently cased directory.',
        );
      directorySpellings.set(ancestor.toLowerCase(), ancestor);
    }
    if (directorySpellings.has(key))
      throw new Error('A portable package file collides with a directory.');
    seen.add(key);
    bytes += regular(file.source).size;
    if (bytes > LIMITS.totalBytes)
      throw new Error('Portable package expanded length exceeds its limit.');
  }
  for (const required of [
    'App/Bezoro.AhkManager.exe',
    'App/resources/app.asar',
    'App/resources/app-update.yml',
    'App/backend/AhkManager.Backend.exe',
    'App/backend/update/AhkManager.UpdateHost.exe',
  ])
    if (!seen.has(required.toLowerCase()))
      throw new Error(`The portable runtime is incomplete: ${required}.`);
  for (const required of [
    'App/Bezoro.AhkManager.exe',
    'App/resources/app.asar',
    'App/resources/app-update.yml',
    'App/backend/AhkManager.Backend.exe',
    'App/backend/update/AhkManager.UpdateHost.exe',
  ])
    if (
      regular(
        candidates.find((file) => file.relative.toLowerCase() === required.toLowerCase()).source,
      ).size === 0
    )
      throw new Error(`The portable runtime file is empty: ${required}.`);
  fs.mkdirSync(outputRoot);
  try {
    const files = candidates.map(({ source, relative }) => {
      const destination = path.join(outputRoot, relative);
      fs.mkdirSync(path.dirname(destination), { recursive: true });
      fs.copyFileSync(source, destination, fs.constants.COPYFILE_EXCL);
      const info = regular(destination);
      return {
        path: relative,
        bytes: info.size,
        sha256: crypto.createHash('sha256').update(fs.readFileSync(destination)).digest('hex'),
      };
    });
    const descriptor = {
      schemaVersion: 1,
      applicationId: APPLICATION_ID,
      updateProtocol: 2,
      version,
      commit,
      files,
    };
    const descriptorBytes = Buffer.from(JSON.stringify(descriptor, null, 2), 'utf8');
    if (descriptorBytes.length > LIMITS.descriptorBytes)
      throw new Error('The portable package inventory exceeds its limit.');
    fs.writeFileSync(path.join(outputRoot, 'portable-package.json'), descriptorBytes, {
      flag: 'wx',
    });
    fs.writeFileSync(
      path.join(outputRoot, 'portable.json'),
      JSON.stringify({ schemaVersion: 1, applicationId: APPLICATION_ID, dataDirectory: 'Data' }),
      { encoding: 'utf8', flag: 'wx' },
    );
    return descriptor;
  } catch (error) {
    fs.rmSync(outputRoot, { recursive: true, force: true });
    throw error;
  }
}

if (require.main === module) {
  const { values } = parseArgs({
    options: {
      source: { type: 'string' },
      launcher: { type: 'string' },
      out: { type: 'string' },
      version: { type: 'string' },
      commit: { type: 'string' },
    },
  });
  const descriptor = stagePortablePackage({
    sourceRoot: values.source,
    launcherPath: values.launcher,
    outputRoot: values.out,
    version: values.version,
    commit: values.commit,
  });
  console.log(
    `Prepared ${descriptor.files.length} portable application files for ${descriptor.version}.`,
  );
}

module.exports = { stagePortablePackage, LIMITS };
