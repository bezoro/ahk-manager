const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

/** Records installed dependency versions, integrity hashes and their license files. */
function writeDependencyLicenses(output) {
  const lock = JSON.parse(fs.readFileSync('package-lock.json', 'utf8'));
  const inventory = [];
  for (const [location, entry] of Object.entries(lock.packages)) {
    if (!location || !location.startsWith('node_modules/')) continue;
    const directory = path.resolve(location),
      manifest = path.join(directory, 'package.json');
    if (!fs.existsSync(manifest)) continue;
    const pkg = JSON.parse(fs.readFileSync(manifest, 'utf8'));
    const key = crypto.createHash('sha256').update(location).digest('hex').slice(0, 16);
    const licenses = [];
    for (const name of fs
      .readdirSync(directory)
      .filter((name) => /^(licen[sc]e|copying|notice)(\.|$)/i.test(name))) {
      const source = path.join(directory, name),
        stat = fs.statSync(source);
      if (!stat.isFile() || stat.size > 2 * 1024 * 1024) continue;
      const target = path.join('licenses', key, name);
      fs.mkdirSync(path.dirname(path.join(output, target)), { recursive: true });
      fs.copyFileSync(source, path.join(output, target));
      licenses.push(target.replaceAll('\\', '/'));
    }
    inventory.push({
      location,
      name: pkg.name,
      version: pkg.version,
      license: pkg.license ?? null,
      integrity: entry.integrity ?? null,
      dev: !!entry.dev,
      licenseFiles: licenses,
    });
  }
  fs.mkdirSync(path.join(output, 'licenses'), { recursive: true });
  fs.writeFileSync(
    path.join(output, 'licenses/dependency-inventory.json'),
    JSON.stringify(
      {
        packageLockSha256: crypto
          .createHash('sha256')
          .update(fs.readFileSync('package-lock.json'))
          .digest('hex'),
        packages: inventory,
      },
      null,
      2,
    ),
  );
}

module.exports = { writeDependencyLicenses };
