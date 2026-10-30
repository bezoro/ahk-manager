export type DeploymentMode = 'installed' | 'portable';

/** Paths shown to the user; no filesystem authority is granted to the renderer. */
export interface StorageState {
  mode: DeploymentMode;
  currentDataDirectory: string;
  defaultDataDirectory: string;
  pendingDataDirectory: string | null;
  canChange: boolean;
  error: string | null;
}

export interface StorageChangeResult {
  state: StorageState;
  restartRequired: boolean;
}
