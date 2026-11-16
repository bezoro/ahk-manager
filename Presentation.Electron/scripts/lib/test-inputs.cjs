const fs = require('node:fs');
const path = require('node:path');
const { parseArgs } = require('node:util');

function testInputs(args) {
  const { values } = parseArgs({
    args,
    options: { out: { type: 'string' }, 'test-name-pattern': { type: 'string' } },
    strict: true,
    allowPositionals: false,
  });
  if (!values.out || !path.isAbsolute(values.out) || fs.existsSync(values.out))
    throw new Error('Supply a fresh absolute --out test staging directory');
  if (values['test-name-pattern'] !== undefined) new RegExp(values['test-name-pattern']);
  return { output: values.out, namePattern: values['test-name-pattern'] };
}

function discoverTests(directory) {
  const walk = (folder) =>
    fs.readdirSync(folder, { withFileTypes: true }).flatMap((entry) => {
      const file = path.join(folder, entry.name);
      if (entry.isDirectory()) return walk(file);
      return entry.isFile() && /\.test\.tsx?$/.test(entry.name) ? [file] : [];
    });
  return ['main', 'renderer']
    .flatMap((scope) => {
      const folder = path.join(directory, scope);
      return fs.existsSync(folder) ? walk(folder) : [];
    })
    .sort();
}

module.exports = { testInputs, discoverTests };
