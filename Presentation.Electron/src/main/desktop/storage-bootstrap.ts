import { spawnSync } from 'node:child_process';
import path from 'node:path';
import type { DeploymentLayout } from '../storage/deployment.js';

/** Runs before Chromium opens data files; initialization never targets an ancestor folder. */
export function initializePortableStorage(layout: DeploymentLayout): void {
  if (layout.mode !== 'portable') return;
  const helper = path.join(
    layout.applicationDirectory,
    'backend',
    'update',
    'AhkManager.UpdateHost.exe',
  );
  const result = spawnSync(helper, ['--initialize-portable'], {
    shell: false,
    windowsHide: true,
    stdio: 'ignore',
  });
  if (result.error || result.status !== 0)
    throw new Error(
      'The portable folder could not be prepared. Extract the complete portable ZIP into its own local folder and check access to the app and data folders.',
    );
}
