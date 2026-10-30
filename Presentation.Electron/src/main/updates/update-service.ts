import { randomUUID } from 'node:crypto';
import type { UpdateChannel, UpdateRelease, UpdateState } from '../../shared/updates.js';
import {
  isTestUpdateVersion,
  validUpdateChannel,
  versionAllowedInChannel,
} from '../../shared/update-versions.js';
import { compareVersions, type RemoteUpdateRelease } from './github-updates.js';
import type { CachedInstaller } from './update-store.js';
import type { UpdateDeployment } from './update-deployment.js';

export interface UpdateStorePort {
  removeLegacyAccess(): Promise<void>;
  readChannel(): Promise<UpdateChannel>;
  saveChannel(channel: UpdateChannel): Promise<void>;
  cached(): Promise<CachedInstaller[]>;
  verify(installer: CachedInstaller): Promise<void>;
  cache(version: string, source: string, sha256: string, bytes: number): Promise<CachedInstaller>;
}
export interface UpdateBoundary {
  version: string;
  deployment?: UpdateDeployment;
  preserveCurrent?(progress: (percent: number) => void): Promise<void>;
  store: UpdateStorePort;
  releases(channel: UpdateChannel): Promise<RemoteUpdateRelease[]>;
  resolve?(id: string): Promise<RemoteUpdateRelease>;
  download(release: RemoteUpdateRelease, progress: (percent: number) => void): Promise<string>;
  preflight(operationId: string, phase: 'begin' | 'commit'): Promise<boolean>;
  confirm(release: UpdateRelease): Promise<boolean>;
  apply(operationId: string, target: CachedInstaller): Promise<void>;
  changed(state: UpdateState): void;
  recovery?(): Promise<string | null>;
}
const active = (state: UpdateState): boolean =>
  ['checking', 'downloading', 'installing'].includes(state.phase);
const safeError = (error: unknown, fallback: string): string =>
  error instanceof Error &&
  !/https?:|token\s+[a-z0-9_]{20}|gh[pousr]_|github_pat_/i.test(error.message)
    ? error.message.slice(0, 2000)
    : fallback;

/** Background checks never download or install. Installation is an explicit serialized action. */
export class UpdateService {
  private state: UpdateState;
  private remote: RemoteUpdateRelease[] = [];
  private local: CachedInstaller[] = [];
  private installing = false;
  private configuringChannel = false;
  private explicitTestOptIn = false;
  constructor(private readonly boundary: UpdateBoundary) {
    this.state = {
      currentVersion: boundary.version,
      channel: 'stable',
      phase: 'idle',
      availableVersion: null,
      lastCheckedAt: null,
      error: null,
      progress: null,
      releases: [],
    };
  }
  get snapshot(): UpdateState {
    return structuredClone(this.state);
  }
  get busy(): boolean {
    return this.installing || this.configuringChannel || active(this.state);
  }
  private publish(patch: Partial<UpdateState> = {}): UpdateState {
    this.state = { ...this.state, ...patch };
    const versions = new Map<string, UpdateRelease>();
    for (const cached of this.local.filter((item) =>
      versionAllowedInChannel(item.version, this.state.channel),
    ))
      versions.set(cached.version, {
        id: `local:${cached.version}`,
        version: cached.version,
        publishedAt: null,
        notes:
          this.boundary.deployment === 'portable'
            ? 'Portable package preserved on this computer.'
            : 'Installer preserved on this computer.',
        commit: null,
        installed: cached.version === this.boundary.version,
        canInstall: cached.version !== this.boundary.version,
        cached: true,
      });
    for (const release of this.remote.filter((item) =>
      versionAllowedInChannel(item.version, this.state.channel),
    ))
      versions.set(release.version, {
        id: release.id,
        version: release.version,
        publishedAt: release.publishedAt,
        notes: release.notes,
        commit: release.commit || null,
        installed: release.version === this.boundary.version,
        canInstall: release.version !== this.boundary.version,
        cached: this.local.some(
          (item) =>
            item.version === release.version &&
            (release.metadataOnly ||
              (item.sha256 === release.sha256 && item.bytes === release.bytes)),
        ),
      });
    if (!versions.has(this.boundary.version))
      versions.set(this.boundary.version, {
        id: 'installed',
        version: this.boundary.version,
        publishedAt: null,
        notes: 'Currently running version.',
        commit: null,
        installed: true,
        canInstall: false,
        cached: false,
      });
    this.state.releases = [...versions.values()]
      .sort((a, b) => compareVersions(b.version, a.version))
      .slice(0, 1000);
    const enteringTests =
      this.explicitTestOptIn &&
      this.state.channel === 'test' &&
      !isTestUpdateVersion(this.boundary.version);
    const testChoices = enteringTests
      ? this.state.releases.filter((item) => item.canInstall && isTestUpdateVersion(item.version))
      : [];
    // An explicit channel switch may select a preview older than the bootstrap.
    // This exception does not survive restart, preventing a stable update from
    // immediately offering the old preview again in a persisted test channel.
    const selectedTest =
      testChoices.find((item) => this.remote.some((remote) => remote.id === item.id)) ??
      testChoices.find((item) => item.cached);
    this.state.availableVersion =
      selectedTest?.version ??
      this.state.releases.find(
        (item) =>
          item.canInstall &&
          versionAllowedInChannel(item.version, this.state.channel) &&
          ((this.state.channel === 'stable' && isTestUpdateVersion(this.boundary.version)) ||
            compareVersions(item.version, this.boundary.version) > 0),
      )?.version ??
      null;
    this.boundary.changed(this.snapshot);
    return this.snapshot;
  }
  async initialize(): Promise<UpdateState> {
    try {
      // Retired credentials are never read or decrypted. A cleanup failure must
      // not prevent public update checks.
      await this.boundary.store.removeLegacyAccess().catch(() => {});
      const channel = await this.boundary.store.readChannel();
      if (!validUpdateChannel(channel)) throw new Error('The saved update channel is invalid.');
      this.local = await this.boundary.store.cached();
      return this.publish({
        channel,
        error: (await this.boundary.recovery?.()) ?? null,
      });
    } catch (error) {
      return this.publish({
        phase: 'error',
        error: safeError(error, 'The update history could not be loaded.'),
      });
    }
  }
  async setChannel(channel: UpdateChannel): Promise<UpdateState> {
    if (this.busy) return this.snapshot;
    if (!validUpdateChannel(channel))
      return this.publish({ phase: 'error', error: 'The update channel is invalid.' });
    this.configuringChannel = true;
    // Discard catalog identities before the first await. A former-channel choice
    // must never remain selectable while its preference is being persisted.
    this.remote = [];
    this.publish({ phase: 'checking', error: null, lastCheckedAt: null });
    try {
      await this.boundary.store.saveChannel(channel);
      this.explicitTestOptIn = channel === 'test' && this.state.channel === 'stable';
      this.publish({ channel, phase: 'idle', error: null });
    } catch {
      return this.publish({
        phase: 'error',
        error: 'The update channel could not be saved. The previous channel was kept.',
      });
    } finally {
      this.configuringChannel = false;
    }
    return this.check();
  }
  async check(): Promise<UpdateState> {
    if (this.busy) return this.snapshot;
    this.publish({ phase: 'checking', error: null, progress: null });
    try {
      this.local = await this.boundary.store.cached();
      this.remote = await this.boundary.releases(this.state.channel);
      const recovery = (await this.boundary.recovery?.()) ?? null;
      return this.publish({
        phase: recovery ? 'error' : 'idle',
        error: recovery,
        lastCheckedAt: new Date().toISOString(),
      });
    } catch (error) {
      return this.publish({
        phase: 'error',
        error: safeError(error, 'Updates could not be checked. Try again later.'),
      });
    }
  }
  async install(id: string): Promise<UpdateState> {
    if (this.busy) return this.snapshot;
    const choice = this.state.releases.find((item) => item.id === id && item.canInstall);
    if (!choice)
      return this.publish({
        phase: 'error',
        error: 'This version is no longer available in the history. Check for updates again.',
      });
    this.installing = true;
    const operationId = randomUUID();
    try {
      if (!(await this.boundary.preflight(operationId, 'begin')))
        return this.publish({
          phase: 'error',
          error: 'Save or discard your changes and wait for operations to finish before updating.',
        });
      if (!(await this.boundary.confirm(choice))) return this.snapshot;
      this.publish({ phase: 'downloading', error: null, progress: 0 });
      const preserving = !!this.boundary.preserveCurrent;
      if (this.boundary.preserveCurrent) {
        await this.boundary.preserveCurrent((percent) =>
          this.publish({ progress: Math.max(0, Math.min(50, percent / 2)) }),
        );
        this.local = await this.boundary.store.cached();
        this.publish({ progress: 50 });
      }
      let remote = this.remote.find((item) => item.id === id);
      let target = this.local.find(
        (item) =>
          item.version === choice.version &&
          (!remote ||
            remote.metadataOnly ||
            (item.sha256 === remote.sha256 && item.bytes === remote.bytes)),
      );
      if (target) {
        try {
          await this.boundary.store.verify(target);
        } catch (error) {
          if (!remote) throw error;
          target = undefined;
        }
      }
      if (!target && remote?.metadataOnly) {
        if (!this.boundary.resolve)
          throw new Error('Check for updates before installing this version.');
        const resolved = await this.boundary.resolve(id);
        if (
          resolved.id !== remote.id ||
          resolved.version !== remote.version ||
          resolved.assetId !== remote.assetId ||
          resolved.bytes !== remote.bytes ||
          resolved.metadataOnly
        )
          throw new Error("This version's identity changed. Check for updates again.");
        remote = resolved;
        this.remote = this.remote.map((item) => (item.id === id ? resolved : item));
      }
      if (!target) {
        if (!remote)
          throw new Error('The installer for this version is not available on this computer.');
        const file = await this.boundary.download(remote, (percent) =>
          this.publish({
            progress: Math.max(0, Math.min(100, preserving ? 50 + percent / 2 : percent)),
          }),
        );
        target = await this.boundary.store.cache(remote.version, file, remote.sha256, remote.bytes);
      }
      await this.boundary.store.verify(target);
      this.local = await this.boundary.store.cached();
      if (!(await this.boundary.preflight(operationId, 'commit')))
        return this.publish({
          phase: 'error',
          progress: null,
          error:
            'The installer has been preserved. Close any dialogs, save your changes, and try installing again.',
        });
      this.publish({ phase: 'installing', progress: 100 });
      await this.boundary.apply(operationId, target);
      return this.snapshot;
    } catch (error) {
      return this.publish({
        phase: 'error',
        progress: null,
        error: safeError(
          error,
          'The update did not finish. The installation and recovery records have been preserved.',
        ),
      });
    } finally {
      this.installing = false;
    }
  }
}
