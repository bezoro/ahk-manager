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
  currentVersion: '', phase: 'idle', availableVersion: null, lastCheckedAt: null,
  error: null, accessConfigured: false, progress: null, releases: [],
});
