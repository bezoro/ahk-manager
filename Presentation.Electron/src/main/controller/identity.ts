import type { ProcessIdentity } from '../../shared/contracts.js';

export function sameProcessIdentity(a: ProcessIdentity | null, b: ProcessIdentity | null): boolean {
  return (
    !!a &&
    !!b &&
    a.pid === b.pid &&
    a.creationFileTime === b.creationFileTime &&
    a.executablePath.toLowerCase() === b.executablePath.toLowerCase() &&
    a.scriptPath.toLowerCase() === b.scriptPath.toLowerCase() &&
    a.userSid === b.userSid &&
    a.sessionId === b.sessionId &&
    a.scriptHash === b.scriptHash &&
    a.interpreterHash === b.interpreterHash
  );
}
