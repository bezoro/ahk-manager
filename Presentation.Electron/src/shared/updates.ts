import {
  compareUpdateVersions,
  parseUpdateVersion,
  type UpdateChannel,
} from './update-versions.js';
export type { UpdateChannel } from './update-versions.js';

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
  channel: UpdateChannel;
  phase: 'idle' | 'checking' | 'downloading' | 'installing' | 'error';
  availableVersion: string | null;
  lastCheckedAt: string | null;
  error: string | null;
  progress: number | null;
  releases: UpdateRelease[];
}

export const emptyUpdateState = (): UpdateState => ({
  currentVersion: '',
  channel: 'stable',
  phase: 'idle',
  availableVersion: null,
  lastCheckedAt: null,
  error: null,
  progress: null,
  releases: [],
});

/** Presentation wording must distinguish installing a newer release from rollback. */
export function isEarlierUpdateVersion(version: string, installedVersion: string): boolean {
  return (
    parseUpdateVersion(version) !== null &&
    parseUpdateVersion(installedVersion) !== null &&
    compareUpdateVersions(version, installedVersion) < 0
  );
}
