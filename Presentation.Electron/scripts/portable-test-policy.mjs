import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { updatePublisherNames } from '../src/main/updates/update-publisher-policy.ts';

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');

/** Test-only metadata uses the same policy reader as the shipped application. */
export async function writePortableTestPolicy({ candidate, baseline, output, commit }) {
  if (
    !/^[a-f0-9]{40}$/.test(commit) ||
    ![candidate, baseline, output].every(
      (value) => typeof value === 'string' && path.isAbsolute(value),
    )
  )
    throw new Error('Use exact source identity and absolute test policy paths.');
  for (const file of [candidate, baseline]) {
    const info = await fs.lstat(file);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1 || info.size > 65536)
      throw new Error('The test policy requires bounded regular application policies.');
  }
  const before = await Promise.all([fs.readFile(candidate), fs.readFile(baseline)]);
  const [names, baselineNames] = await Promise.all([
    updatePublisherNames(candidate),
    updatePublisherNames(baseline),
  ]);
  const after = await Promise.all([fs.readFile(candidate), fs.readFile(baseline)]);
  if (
    !before[0].equals(before[1]) ||
    !before[0].equals(after[0]) ||
    !before[1].equals(after[1]) ||
    JSON.stringify(names) !== JSON.stringify(baselineNames)
  )
    throw new Error('The candidate and baseline must retain the same unchanged publisher policy.');
  const result = {
    schemaVersion: 1,
    names,
    candidatePolicySha256: sha256(before[0]),
    baselinePolicySha256: sha256(before[1]),
    sourceCommit: commit,
  };
  await fs.writeFile(output, JSON.stringify(result, null, 2) + '\n', {
    encoding: 'utf8',
    flag: 'wx',
  });
  return result;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { values } = parseArgs({
    options: {
      candidate: { type: 'string' },
      baseline: { type: 'string' },
      output: { type: 'string' },
      commit: { type: 'string' },
    },
  });
  await writePortableTestPolicy(values);
  console.log(
    'Recorded the exact application publisher policy for isolated portable signing tests.',
  );
}
