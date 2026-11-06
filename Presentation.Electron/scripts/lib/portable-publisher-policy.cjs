const fs = require('node:fs');
const path = require('node:path');

/** The native portable updater reads this exact, packaged publisher policy. */
function writePortablePublisherPolicy(applicationRoot, publisherName) {
  const target = path.join(applicationRoot, 'resources', 'app-update.yml');
  const policy = {
    provider: 'github',
    owner: 'bezoro',
    repo: 'ahk-manager',
    private: false,
    ...(publisherName !== null ? { publisherName: [publisherName] } : {}),
  };
  const bytes = Buffer.from(JSON.stringify(policy, null, 2) + '\n');
  fs.mkdirSync(path.dirname(target), { recursive: true });
  if (fs.existsSync(target)) {
    if (!fs.readFileSync(target).equals(bytes))
      throw new Error('The packaged publisher policy differs from the configured portable policy.');
    return;
  }
  fs.writeFileSync(target, bytes, { flag: 'wx' });
}
module.exports = { writePortablePublisherPolicy };
