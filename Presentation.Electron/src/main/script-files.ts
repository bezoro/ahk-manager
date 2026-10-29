import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import type { ScriptFileIdentity } from '../shared/contracts.js';

/** Independent host-side stamp. The backend additionally verifies the Windows file ID. */
export async function scriptFileStamp(file: ScriptFileIdentity): Promise<string> {
  const target = path.win32.normalize(file.path);
  if (
    !/^[a-z]:\\/i.test(target) ||
    target.startsWith('\\\\') ||
    !target.toLowerCase().endsWith('.ahk')
  )
    throw new Error('Invalid script path');
  for (let current = target; ; current = path.win32.dirname(current)) {
    const info = await fs.lstat(current, { bigint: true });
    if (info.isSymbolicLink() || (current === target && (!info.isFile() || info.nlink !== 1n)))
      throw new Error('Redirected or multiply linked script');
    const parent = path.win32.dirname(current);
    if (parent === current) break;
  }
  const handle = await fs.open(target, 'r');
  try {
    const before = await handle.stat({ bigint: true });
    if (!before.isFile() || before.nlink !== 1n || before.size > 64n * 1024n * 1024n)
      throw new Error('Uncertain script identity');
    const hash = createHash('sha256')
      .update(await handle.readFile())
      .digest('hex');
    const after = await handle.stat({ bigint: true });
    const stamp = (s: typeof before) =>
      [s.dev, s.ino, s.size, s.birthtimeNs, s.mtimeNs, s.ctimeNs].join(':');
    if (hash !== file.hash || stamp(before) !== stamp(after))
      throw new Error('Script identity changed');
    return stamp(after) + ':' + hash;
  } finally {
    await handle.close();
  }
}
