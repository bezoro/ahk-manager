import { getStorageState } from './data-migration.js';
import type { DeploymentLayout } from './deployment.js';
import { openNativeStorageLease, type StartStorageLease } from './native-storage-lease.js';

export interface DataMoveBoundary {
  check(): void;
  beforePromotion(): Promise<void>;
}

const noLease: DataMoveBoundary = { check: () => {}, beforePromotion: async () => {} };

/** A native child pins the source, private staging folder and their namespace while copying. */
export async function withDataMoveLease<T>(
  layout: DeploymentLayout,
  action: (boundary: DataMoveBoundary) => Promise<T>,
  start?: StartStorageLease,
): Promise<T> {
  if (layout.mode !== 'portable' || !getStorageState(layout).pendingDataDirectory)
    return action(noLease);
  const lease = await openNativeStorageLease(layout, '--lease-portable-data-move', start);
  try {
    const result = await action({ check: lease.check, beforePromotion: lease.releaseStage });
    lease.check();
    return result;
  } finally {
    await lease.close();
  }
}
