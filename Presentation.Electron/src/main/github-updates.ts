import {
  compareUpdateVersions,
  isTestUpdateVersion,
  validUpdateChannel,
  versionAllowedInChannel,
  type UpdateChannel,
} from '../shared/update-versions.js';
export { validUpdateVersion } from '../shared/update-versions.js';

const REPOSITORY_API = 'https://api.github.com/repos/bezoro/ahk-manager';
const MANIFEST_NAME = 'ahk-manager-release.json';
const METADATA_LIMIT = 2 * 1024 * 1024;
const MANIFEST_LIMIT = 64 * 1024;
const INSTALLER_LIMIT = 1024 * 1024 * 1024;
const ASSET_HOSTS = new Set([
  'release-assets.githubusercontent.com',
  'objects.githubusercontent.com',
]);

export interface RemoteUpdateRelease {
  id: string;
  version: string;
  publishedAt: string;
  notes: string;
  commit: string;
  assetId: number;
  sha256: string;
  sha512: string;
  bytes: number;
  installerName: string;
  metadataOnly?: boolean;
}

export const compareVersions = compareUpdateVersions;

export function assetUrl(id: number): string {
  if (!Number.isSafeInteger(id) || id <= 0) throw new Error('The update identifier is invalid.');
  return `${REPOSITORY_API}/releases/assets/${id}`;
}

const object = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const positiveId = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
const exactCommit = (value: unknown): value is string =>
  typeof value === 'string' && value.length === 40 && /^[a-f0-9]{40}$/.test(value);
const isoDate = (value: unknown): value is string =>
  typeof value === 'string' &&
  value.length <= 40 &&
  /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z$/.test(value) &&
  Number.isFinite(Date.parse(value)) &&
  new Date(value).toISOString().slice(0, 19) === value.slice(0, 19);

class UpdateRequestError extends Error {}
const invalidManifest = (): UpdateRequestError =>
  new UpdateRequestError(
    'The published release has invalid update metadata. No files were installed.',
  );

function exactKeys(value: Record<string, unknown>, keys: string[]): boolean {
  return (
    Object.keys(value).length === keys.length && keys.every((key) => Object.hasOwn(value, key))
  );
}

// JSON.parse already verifies grammar. This pass rejects duplicate object keys,
// including escaped spellings, before accepting security-relevant metadata.
function parseJson(text: string): unknown {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw invalidManifest();
  }
  const stack: Array<{ object: boolean; key: boolean; keys: Set<string> }> = [];
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (character === '"') {
      const start = index++;
      for (; index < text.length; index++) {
        if (text[index] === '\\') index++;
        else if (text[index] === '"') break;
      }
      const scope = stack.at(-1);
      if (scope?.object && scope.key) {
        const key = JSON.parse(text.slice(start, index + 1)) as string;
        if (scope.keys.has(key)) throw invalidManifest();
        scope.keys.add(key);
      }
    } else if (character === '{' || character === '[')
      stack.push({ object: character === '{', key: character === '{', keys: new Set() });
    else if (character === '}' || character === ']') stack.pop();
    else if (character === ':' && stack.at(-1)?.object) stack.at(-1)!.key = false;
    else if (character === ',' && stack.at(-1)?.object) stack.at(-1)!.key = true;
  }
  return parsed;
}

interface ReleaseAsset {
  id: number;
  name: string;
  size: number;
}
interface Candidate {
  id: number;
  version: string;
  publishedAt: string;
  target: string;
  notes: string;
  manifest: ReleaseAsset;
  installer: ReleaseAsset;
}

function candidate(value: unknown, channel: UpdateChannel): Candidate | null {
  if (!object(value) || value.draft !== false || typeof value.tag_name !== 'string') return null;
  const version = value.tag_name.startsWith('v') ? value.tag_name.slice(1) : '';
  if (
    !versionAllowedInChannel(version, channel) ||
    value.prerelease !== isTestUpdateVersion(version)
  )
    return null;
  if (!Array.isArray(value.assets) || value.assets.length > 100) throw invalidManifest();
  const manifests = value.assets.filter((asset) => object(asset) && asset.name === MANIFEST_NAME);
  if (!manifests.length) return null; // Releases preceding update protocol 1 stay outside this channel.
  const installerName = `AHK-Manager-${version}-Setup-x64.exe`;
  const installers = value.assets.filter((asset) => object(asset) && asset.name === installerName);
  if (
    manifests.length !== 1 ||
    installers.length !== 1 ||
    !positiveId(value.id) ||
    !isoDate(value.published_at) ||
    typeof value.target_commitish !== 'string' ||
    (isTestUpdateVersion(version) && !exactCommit(value.target_commitish))
  )
    throw invalidManifest();
  const asset = (input: unknown, maximum: number): ReleaseAsset => {
    if (
      !object(input) ||
      !positiveId(input.id) ||
      typeof input.name !== 'string' ||
      typeof input.size !== 'number' ||
      !Number.isSafeInteger(input.size) ||
      input.size <= 0 ||
      input.size > maximum ||
      input.state !== 'uploaded'
    )
      throw invalidManifest();
    return { id: input.id, name: input.name, size: input.size };
  };
  return {
    id: value.id,
    version,
    publishedAt: value.published_at,
    target: value.target_commitish,
    notes: typeof value.body === 'string' ? value.body.slice(0, 8000).replaceAll('\0', '') : '',
    manifest: asset(manifests[0], MANIFEST_LIMIT),
    installer: asset(installers[0], INSTALLER_LIMIT),
  };
}

function releaseFromMetadata(release: Candidate): RemoteUpdateRelease {
  return {
    id: `github:${release.id}`,
    version: release.version,
    publishedAt: release.publishedAt,
    notes: release.notes,
    commit: exactCommit(release.target) ? release.target : '',
    assetId: release.installer.id,
    sha256: '',
    sha512: '',
    bytes: release.installer.size,
    installerName: release.installer.name,
    metadataOnly: true,
  };
}

function releaseFromManifest(value: unknown, release: Candidate): RemoteUpdateRelease {
  if (
    !object(value) ||
    !exactKeys(value, [
      'schemaVersion',
      'updateProtocol',
      'version',
      'commit',
      'publishedAt',
      'notes',
      'installer',
    ]) ||
    value.schemaVersion !== 1 ||
    value.updateProtocol !== 1 ||
    value.version !== release.version ||
    !exactCommit(value.commit) ||
    (release.target !== value.commit &&
      (isTestUpdateVersion(release.version) || release.target !== 'main')) ||
    !isoDate(value.publishedAt) ||
    typeof value.notes !== 'string' ||
    value.notes.length > 8000 ||
    value.notes.includes('\0')
  )
    throw invalidManifest();
  const installer = value.installer;
  if (
    !object(installer) ||
    !exactKeys(installer, ['name', 'sha256', 'sha512', 'bytes']) ||
    installer.name !== release.installer.name ||
    installer.bytes !== release.installer.size ||
    typeof installer.sha256 !== 'string' ||
    !/^[a-f0-9]{64}$/.test(installer.sha256) ||
    typeof installer.sha512 !== 'string' ||
    !/^[A-Za-z0-9+/]{86}==$/.test(installer.sha512)
  )
    throw invalidManifest();
  const hash = Buffer.from(installer.sha512, 'base64');
  if (hash.length !== 64 || hash.toString('base64') !== installer.sha512) throw invalidManifest();
  return {
    id: `github:${release.id}`,
    version: release.version,
    publishedAt: release.publishedAt,
    notes: value.notes,
    commit: value.commit,
    assetId: release.installer.id,
    sha256: installer.sha256,
    sha512: installer.sha512,
    bytes: release.installer.size,
    installerName: release.installer.name,
  };
}

export class GitHubUpdateClient {
  private readonly responses = new Map<string, { etag: string; value: unknown }>();
  private readonly manifests = new Map<string, RemoteUpdateRelease>();
  private listed = new Map<string, Candidate>();
  private channel: UpdateChannel = 'stable';
  private generation = 0;
  constructor(private readonly fetcher: typeof fetch = globalThis.fetch) {}

  async releases(channel: UpdateChannel = 'stable'): Promise<RemoteUpdateRelease[]> {
    try {
      if (!validUpdateChannel(channel))
        throw new UpdateRequestError('The update channel is invalid.');
      if (channel !== this.channel) {
        this.channel = channel;
        this.generation++;
        this.responses.clear();
        this.manifests.clear();
        this.listed.clear();
      }
      const generation = this.generation;
      const found = new Map<string, Candidate>();
      const versions = new Set<string>();
      let truncated = false;
      for (let page = 1; page <= 3; page++) {
        const rows = await this.json(
          `${REPOSITORY_API}/releases?per_page=100&page=${page}`,
          METADATA_LIMIT,
          true,
          generation,
        );
        if (!Array.isArray(rows) || rows.length > 100) throw invalidManifest();
        for (const row of rows) {
          const item = candidate(row, channel);
          if (!item) continue;
          const id = `github:${item.id}`;
          if (versions.has(item.version) || found.has(id)) throw invalidManifest();
          versions.add(item.version);
          found.set(id, item);
        }
        if (rows.length < 100) break;
        if (page === 3) truncated = true;
      }
      // Test builds share the GitHub release listing. Preserve the latest stable
      // even when frequent previews fill the bounded three-page history window.
      if (truncated) {
        const latest = await this.json(
          `${REPOSITORY_API}/releases/latest`,
          METADATA_LIMIT,
          true,
          generation,
          true,
        );
        if (latest !== null) {
          if (!object(latest) || latest.draft !== false || latest.prerelease !== false)
            throw invalidManifest();
          const item = candidate(latest, 'stable');
          if (item) {
            const id = `github:${item.id}`;
            const existing = found.get(id);
            if (existing) {
              if (JSON.stringify(existing) !== JSON.stringify(item)) throw invalidManifest();
            } else {
              if (versions.has(item.version)) throw invalidManifest();
              versions.add(item.version);
              found.set(id, item);
            }
          }
        }
      }
      const sorted = [...found.values()].sort((a, b) => compareVersions(b.version, a.version));
      // Resolve only the newest manifest during polling. Older releases are
      // resolved immediately before installation, keeping public API use bounded.
      if (sorted[0]) await this.resolveCandidate(sorted[0], generation);
      const result = sorted.map((item) => {
        const cached = this.cached(item);
        return cached ? { ...cached, publishedAt: item.publishedAt } : releaseFromMetadata(item);
      });
      this.requireGeneration(generation);
      this.listed = found;
      return result;
    } catch (error) {
      if (error instanceof UpdateRequestError) throw error;
      throw new UpdateRequestError(
        'Updates could not be checked. Check your connection and try again.',
      );
    }
  }

  async resolve(id: string): Promise<RemoteUpdateRelease> {
    try {
      const generation = this.generation;
      const item = this.listed.get(id);
      if (!item)
        throw new UpdateRequestError(
          'This version is not in the checked history. Check for updates and try again.',
        );
      return {
        ...(await this.resolveCandidate(item, generation)),
        publishedAt: item.publishedAt,
      };
    } catch (error) {
      if (error instanceof UpdateRequestError) throw error;
      throw new UpdateRequestError(
        'This version could not be checked. Check your connection and try again.',
      );
    }
  }

  private requireGeneration(generation: number): void {
    if (this.generation !== generation)
      throw new UpdateRequestError(
        'The update channel changed during the request. Check for updates again.',
      );
  }

  private key(item: Candidate): string {
    return `${item.id}:${item.manifest.id}:${item.installer.id}`;
  }

  private cached(item: Candidate): RemoteUpdateRelease | undefined {
    const release = this.manifests.get(this.key(item));
    if (
      release &&
      (release.version !== item.version ||
        release.bytes !== item.installer.size ||
        release.installerName !== item.installer.name ||
        (item.target !== release.commit &&
          (isTestUpdateVersion(item.version) || item.target !== 'main')))
    )
      throw invalidManifest();
    return release;
  }

  private async resolveCandidate(
    item: Candidate,
    generation: number,
  ): Promise<RemoteUpdateRelease> {
    this.requireGeneration(generation);
    const cached = this.cached(item);
    if (cached) return cached;
    const release = releaseFromManifest(
      await this.json(assetUrl(item.manifest.id), MANIFEST_LIMIT, false, generation),
      item,
    );
    this.manifests.set(this.key(item), release);
    if (this.manifests.size > 600) this.manifests.delete(this.manifests.keys().next().value!);
    return release;
  }

  private async json(
    url: string,
    maximum: number,
    metadata: boolean,
    generation: number,
    optionalNotFound = false,
  ): Promise<unknown> {
    this.requireGeneration(generation);
    let location = new URL(url);
    const cached = metadata ? this.responses.get(url) : undefined;
    const signal = AbortSignal.timeout(15000);
    for (let redirects = 0; redirects <= 3; redirects++) {
      const api = location.origin === 'https://api.github.com';
      if (
        location.protocol !== 'https:' ||
        location.username ||
        location.password ||
        location.port ||
        location.hash ||
        (!api && !ASSET_HOSTS.has(location.hostname))
      )
        throw invalidManifest();
      const headers: Record<string, string> = {
        Accept: metadata ? 'application/vnd.github+json' : 'application/octet-stream',
        'User-Agent': 'Bezoro-AhkManager-Updater',
      };
      if (api) {
        headers['X-GitHub-Api-Version'] = '2022-11-28';
        if (cached && metadata) headers['If-None-Match'] = cached.etag;
      }
      const response = await this.fetcher(location.href, {
        method: 'GET',
        headers,
        redirect: 'manual',
        credentials: 'omit',
        signal,
      });
      this.requireGeneration(generation);
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        const redirect = response.headers.get('location');
        await response.body?.cancel();
        if (!redirect || redirects === 3) throw invalidManifest();
        const next = new URL(redirect, location);
        // API requests never accept another repository or an authentication page.
        if (next.origin === 'https://api.github.com' || !ASSET_HOSTS.has(next.hostname))
          throw invalidManifest();
        location = next;
        continue;
      }
      if (response.status === 304 && cached && metadata && api) return cached.value;
      if (response.status === 404 && optionalNotFound) {
        await response.body?.cancel();
        return null;
      }
      if (
        response.status === 429 ||
        (response.status === 403 && response.headers.get('x-ratelimit-remaining') === '0')
      ) {
        await response.body?.cancel();
        throw new UpdateRequestError(
          'GitHub has temporarily limited update checks. Wait before checking again.',
        );
      }
      if ([401, 403, 404].includes(response.status)) {
        await response.body?.cancel();
        throw new UpdateRequestError(
          'The public GitHub releases could not be accessed. Check your connection and try again.',
        );
      }
      if (response.status !== 200 || !response.body) {
        await response.body?.cancel();
        throw new UpdateRequestError(
          'GitHub did not respond to the update check. Try again later.',
        );
      }
      const length = response.headers.get('content-length');
      if (length !== null && (!/^\d+$/.test(length) || Number(length) > maximum)) {
        await response.body.cancel();
        throw invalidManifest();
      }
      const reader = response.body.getReader(),
        parts: Uint8Array[] = [];
      let bytes = 0;
      try {
        for (;;) {
          const next = await reader.read();
          if (next.done) break;
          bytes += next.value.byteLength;
          if (bytes > maximum) {
            await reader.cancel();
            throw invalidManifest();
          }
          parts.push(next.value);
        }
      } finally {
        reader.releaseLock();
      }
      let text: string;
      try {
        text = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(parts));
      } catch {
        throw invalidManifest();
      }
      const value = parseJson(text);
      this.requireGeneration(generation);
      const etag = response.headers.get('etag');
      if (metadata && api && etag && etag.length <= 512) this.responses.set(url, { etag, value });
      return value;
    }
    throw invalidManifest();
  }
}
