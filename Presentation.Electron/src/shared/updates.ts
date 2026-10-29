export interface UpdateRelease {
  id: string;
  version: string;
  publishedAt: string | null;
  notes: string;
  commit: string | null;
  installed: boolean;
  canInstall: boolean;
  cached: boolean;
}

export interface UpdateState {
  currentVersion: string;
  phase: 'idle' | 'checking' | 'downloading' | 'installing' | 'error';
  availableVersion: string | null;
  lastCheckedAt: string | null;
  error: string | null;
  accessConfigured: boolean;
  progress: number | null;
  releases: UpdateRelease[];
}

export const emptyUpdateState = (): UpdateState => ({
  currentVersion: '',
  phase: 'idle',
  availableVersion: null,
  lastCheckedAt: null,
  error: null,
  accessConfigured: false,
  progress: null,
  releases: [],
});

/** Presentation wording must distinguish installing a newer release from rollback. */
export function isEarlierUpdateVersion(version: string, installedVersion: string): boolean {
  if (
    ![version, installedVersion].every((value) =>
      /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value),
    )
  )
    return false;
  const target = version.split('.').map(Number),
    installed = installedVersion.split('.').map(Number);
  if (![...target, ...installed].every((value) => Number.isSafeInteger(value))) return false;
  for (let index = 0; index < 3; index++) {
    if (target[index] !== installed[index]) return target[index]! < installed[index]!;
  }
  return false;
}
