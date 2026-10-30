import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { portableManifestFile, portableSignatureFile } from './update-deployment.js';
import { readPortablePackageProof } from './portable-package-proof.js';
import { updatePublisherNames } from './update-publisher-policy.js';

export interface PortableSignatureOptions {
  policyPath?: string;
  helperPath?: string;
  verify?: (manifestPath: string, signaturePath: string, publishers: string[]) => Promise<string>;
}

async function regular(file: string, maximum: number): Promise<import('node:fs').Stats> {
  const info = await fs.lstat(file);
  if (
    !info.isFile() ||
    info.isSymbolicLink() ||
    info.nlink !== 1 ||
    info.size < 1 ||
    info.size > maximum
  )
    throw new Error('The portable package verification record is invalid.');
  return info;
}

/** The native verifier uses Windows certificate trust and the configured publisher names. */
export async function verifyPortableManifestNative(
  manifestPath: string,
  signaturePath: string,
  publishers: string[],
  helperPath = path.join(
    path.dirname(process.execPath),
    'backend',
    'update',
    'AhkManager.UpdateHost.exe',
  ),
): Promise<string> {
  const request = JSON.stringify({ manifestPath, signaturePath, publishers });
  if (Buffer.byteLength(request, 'utf8') > 65536)
    throw new Error('The portable signature request is invalid.');
  return new Promise<string>((resolve, reject) => {
    const child = spawn(helperPath, ['--verify-portable-manifest'], {
      shell: false,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let done = false;
    let output = '';
    let overflow = false;
    const finish = (manifestSha256: string | null) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      child.stdin.destroy();
      child.stdout.destroy();
      child.stderr.destroy();
      child.unref();
      if (manifestSha256) resolve(manifestSha256);
      else
        reject(
          new Error("The portable package signature does not match this application's publisher."),
        );
    };
    const timer = setTimeout(() => finish(null), 15000);
    child.stdout.on('data', (chunk: Buffer) => {
      if (output.length + chunk.length > 4096) overflow = true;
      else output += chunk.toString('utf8');
    });
    child.stderr.on('data', () => {});
    child.once('error', () => finish(null));
    child.stdin.once('error', () => finish(null));
    child.once('close', (code) => {
      let manifestSha256: string | null = null;
      try {
        const value: unknown = JSON.parse(output);
        if (
          !overflow &&
          code === 0 &&
          !!value &&
          typeof value === 'object' &&
          !Array.isArray(value) &&
          Object.keys(value).length === 2 &&
          (value as Record<string, unknown>).valid === true &&
          typeof (value as Record<string, unknown>).manifestSha256 === 'string' &&
          /^[a-f0-9]{64}$/.test((value as Record<string, unknown>).manifestSha256 as string)
        )
          manifestSha256 = (value as Record<string, unknown>).manifestSha256 as string;
      } catch {
        /* A verifier must produce its complete fixed acknowledgement. */
      }
      finish(manifestSha256);
    });
    child.stdin.end(request, 'utf8');
  });
}

async function verifyManifestHash(
  manifestPath: string,
  signaturePath: string,
  publishers: string[],
  expectedSha256: string,
  options: PortableSignatureOptions,
): Promise<void> {
  await regular(signaturePath, 65536).catch(() => {
    throw new Error(
      'This portable release has no valid publisher signature. No files were installed.',
    );
  });
  const verifiedSha256 = options.verify
    ? await options.verify(manifestPath, signaturePath, publishers)
    : await verifyPortableManifestNative(
        manifestPath,
        signaturePath,
        publishers,
        options.helperPath,
      );
  if (verifiedSha256 !== expectedSha256)
    throw new Error('The portable release metadata changed during publisher verification.');
}

export async function verifyPortableManifestPolicy(
  manifestPath: string,
  signaturePath: string,
  options: PortableSignatureOptions = {},
): Promise<void> {
  const publishers = await updatePublisherNames(options.policyPath);
  if (!publishers.length) return;
  await regular(manifestPath, 65536);
  const manifestSha256 = createHash('sha256')
    .update(await fs.readFile(manifestPath))
    .digest('hex');
  await verifyManifestHash(manifestPath, signaturePath, publishers, manifestSha256, options);
}

/** Cached rollback packages retain the same publisher policy as new downloads. */
export async function verifyCachedPortablePackageSignature(
  file: string,
  options: PortableSignatureOptions = {},
): Promise<void> {
  const publishers = await updatePublisherNames(options.policyPath);
  if (!publishers.length) return;
  const directory = path.dirname(file);
  const manifest = path.join(directory, portableManifestFile);
  const signature = path.join(directory, portableSignatureFile);
  const info = await regular(file, 1024 * 1024 * 1024);
  await regular(manifest, 65536);
  const manifestBytes = await fs.readFile(manifest);
  const proof = readPortablePackageProof(
    new TextDecoder('utf-8', { fatal: true }).decode(manifestBytes),
  );
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  const after = await regular(file, 1024 * 1024 * 1024);
  if (
    proof.version !== path.basename(directory) ||
    proof.bytes !== info.size ||
    proof.sha256 !== hash.digest('hex') ||
    info.ino !== after.ino ||
    info.dev !== after.dev ||
    info.size !== after.size ||
    info.mtimeMs !== after.mtimeMs ||
    info.ctimeMs !== after.ctimeMs
  )
    throw new Error('The portable package does not match its signed release metadata.');
  // The native acknowledgement identifies its exact authenticated bytes. A
  // replacement manifest cannot authenticate a different archive proof here.
  await verifyManifestHash(
    manifest,
    signature,
    publishers,
    createHash('sha256').update(manifestBytes).digest('hex'),
    options,
  );
}
