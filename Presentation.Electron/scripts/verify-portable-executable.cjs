const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { parseArgs } = require('node:util');
const { NtExecutable, NtExecutableResource, Resource } = require('resedit');
const { validUpdateVersion } = require('./lib/update-versions.cjs');

function numericVersion(value) {
  if (!/^\d+\.\d+\.\d+(?:\.0)?$/.test(value)) return null;
  return value.split('.').slice(0, 3).map(Number).join('.');
}

/** Inspect the final PE without executing or modifying it. */
function verifyPortableExecutable(file, version) {
  if (!path.isAbsolute(file) || !validUpdateVersion(version))
    throw new Error('An absolute executable path and valid release version are required.');
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 1024 ** 3)
    throw new Error('A bounded regular executable is required.');
  const bytes = fs.readFileSync(file);
  const resources = NtExecutableResource.from(NtExecutable.from(bytes));
  const manifests = resources.entries.filter((entry) => entry.type === 24 && entry.id === 1);
  if (!manifests.length) throw new Error('The executable has no embedded application manifest.');
  for (const entry of manifests) {
    const raw = Buffer.from(entry.bin);
    const xml =
      raw[0] === 0xff && raw[1] === 0xfe
        ? raw.subarray(2).toString('utf16le')
        : raw.toString('utf8');
    const levels = [...xml.matchAll(/<(?:[\w.-]+:)?requestedExecutionLevel\b([^>]*)>/g)];
    if (
      levels.length !== 1 ||
      !/\blevel\s*=\s*(['"])requireAdministrator\1/.test(levels[0][1]) ||
      !/\buiAccess\s*=\s*(['"])false\1/.test(levels[0][1]) ||
      (levels[0][1].match(/\blevel\s*=/g) ?? []).length !== 1 ||
      (levels[0][1].match(/\buiAccess\s*=/g) ?? []).length !== 1
    )
      throw new Error(
        'The packaged executable must require administrator access without UIAccess.',
      );
  }
  const versions = Resource.VersionInfo.fromEntries(resources.entries);
  if (versions.length !== 1) throw new Error('The executable has ambiguous version resources.');
  const strings = versions[0].getStringValues({ lang: 0x0409, codepage: 1200 });
  const fileVersion = strings.FileVersion ?? '';
  const productVersion = strings.ProductVersion ?? '';
  const core = version.split('-')[0];
  if (
    (fileVersion !== version && (version.includes('-') || numericVersion(fileVersion) !== core)) ||
    numericVersion(productVersion) !== core
  )
    throw new Error('The packaged executable version does not match the release identity.');
  return {
    schemaVersion: 1,
    fileName: path.basename(file),
    version,
    fileVersion,
    productVersion,
    requestedExecutionLevel: 'requireAdministrator',
    uiAccess: false,
    manifestResources: manifests.length,
    sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
    bytes: bytes.length,
    readOnly: true,
  };
}

if (require.main === module) {
  try {
    const { values } = parseArgs({
      options: { file: { type: 'string' }, version: { type: 'string' }, out: { type: 'string' } },
      strict: true,
      allowPositionals: false,
    });
    if (!values.file || !values.version || !values.out || !path.isAbsolute(values.out))
      throw new Error('Use --file, --version and a fresh absolute --out path.');
    const result = verifyPortableExecutable(values.file, values.version);
    fs.writeFileSync(values.out, JSON.stringify(result, null, 2) + '\n', {
      flag: 'wx',
      mode: 0o600,
    });
    process.stdout.write(`Verified packaged executable elevation and version ${values.version}.\n`);
  } catch (error) {
    process.stderr.write((error instanceof Error ? error.message : String(error)) + '\n');
    process.exitCode = 1;
  }
}

module.exports = { verifyPortableExecutable };
