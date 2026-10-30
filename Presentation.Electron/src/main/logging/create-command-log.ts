import fs from 'node:fs';
import path from 'node:path';
import { CommandLog } from './command-log.js';
import { assertStoragePath, type DeploymentLayout } from '../storage/deployment.js';

/** Installed startup logs stay outside the profile migration destination. */
export function createCommandLog(
  deployment: Pick<DeploymentLayout, 'mode' | 'dataDirectory'>,
  localAppDataDirectory: string,
): CommandLog {
  if (deployment.mode === 'portable') return new CommandLog(deployment.dataDirectory);
  const diagnosticsRoot = path.join(
    localAppDataDirectory,
    'Bezoro',
    'AhkManagerElectronDiagnostics',
  );
  try {
    if (!path.isAbsolute(localAppDataDirectory) || localAppDataDirectory.startsWith('\\\\'))
      throw new Error('A local absolute diagnostics directory is required.');
    assertStoragePath(diagnosticsRoot);
    fs.mkdirSync(diagnosticsRoot, { recursive: true, mode: 0o700 });
    assertStoragePath(diagnosticsRoot);
  } catch {
    // CommandLog reports unavailable storage; diagnostics never block application startup.
  }
  return new CommandLog(diagnosticsRoot);
}
