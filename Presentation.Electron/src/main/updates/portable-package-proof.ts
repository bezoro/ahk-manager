import { validUpdateVersion } from '../../shared/update-versions.js';
import { releasePackageName } from './update-deployment.js';
import { invalidUpdateMetadata, parseUpdateJson } from './update-metadata.js';

export interface PortablePackageProof {
  version: string;
  commit: string;
  sha256: string;
  sha512: string;
  bytes: number;
}

const object = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const keys = (value: Record<string, unknown>, expected: string[]): boolean =>
  Object.keys(value).length === expected.length &&
  expected.every((key) => Object.hasOwn(value, key));

/** The raw manifest is retained unchanged because its detached signature binds those bytes. */
export function readPortablePackageProof(text: string): PortablePackageProof {
  if (Buffer.byteLength(text, 'utf8') > 65536) throw invalidUpdateMetadata();
  const value = parseUpdateJson(text);
  if (
    !object(value) ||
    !keys(value, [
      'schemaVersion',
      'updateProtocol',
      'version',
      'commit',
      'publishedAt',
      'notes',
      'archive',
    ]) ||
    value.schemaVersion !== 1 ||
    value.updateProtocol !== 2 ||
    !validUpdateVersion(value.version) ||
    typeof value.commit !== 'string' ||
    !/^[a-f0-9]{40}$/.test(value.commit) ||
    typeof value.publishedAt !== 'string' ||
    !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z$/.test(value.publishedAt) ||
    !Number.isFinite(Date.parse(value.publishedAt)) ||
    new Date(value.publishedAt).toISOString().slice(0, 19) !== value.publishedAt.slice(0, 19) ||
    typeof value.notes !== 'string' ||
    value.notes.length > 8000 ||
    value.notes.includes('\0')
  )
    throw invalidUpdateMetadata();
  const archive = value.archive;
  if (
    !object(archive) ||
    !keys(archive, ['name', 'bytes', 'sha256', 'sha512']) ||
    archive.name !== releasePackageName(value.version, 'portable') ||
    typeof archive.bytes !== 'number' ||
    !Number.isSafeInteger(archive.bytes) ||
    archive.bytes < 1 ||
    archive.bytes > 1024 * 1024 * 1024 ||
    typeof archive.sha256 !== 'string' ||
    !/^[a-f0-9]{64}$/.test(archive.sha256) ||
    typeof archive.sha512 !== 'string' ||
    !/^[A-Za-z0-9+/]{86}==$/.test(archive.sha512)
  )
    throw invalidUpdateMetadata();
  const hash = Buffer.from(archive.sha512, 'base64');
  if (hash.length !== 64 || hash.toString('base64') !== archive.sha512)
    throw invalidUpdateMetadata();
  return {
    version: value.version,
    commit: value.commit,
    bytes: archive.bytes,
    sha256: archive.sha256,
    sha512: archive.sha512,
  };
}

export function requirePortablePackageProof(
  text: string,
  expected: { version: string; sha256: string; bytes: number },
): PortablePackageProof {
  const proof = readPortablePackageProof(text);
  if (
    proof.version !== expected.version ||
    proof.sha256 !== expected.sha256 ||
    proof.bytes !== expected.bytes
  )
    throw new Error('The portable update metadata does not identify this package.');
  return proof;
}
