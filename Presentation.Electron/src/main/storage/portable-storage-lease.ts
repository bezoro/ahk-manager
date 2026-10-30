import type { DeploymentLayout } from './deployment.js';
import { openNativeStorageLease, type StartStorageLease } from './native-storage-lease.js';

export interface PortableStorageLease {
  check(): void;
  close(): Promise<void>;
}

/** Remains owned by the main process until its final, graceful application shutdown. */
export async function acquirePortableStorageLease(
  layout: DeploymentLayout,
  onFailure?: (error: Error) => void,
  start?: StartStorageLease,
): Promise<PortableStorageLease> {
  if (layout.mode !== 'portable') return { check: () => {}, close: async () => {} };
  const lease = await openNativeStorageLease(layout, '--lease-portable-storage', start, onFailure);
  return { check: lease.check, close: lease.close };
}
