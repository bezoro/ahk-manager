/** Portable protocol identifiers remain compatible with existing portable releases. */
export type UpdateDeployment = 'portable';

export function validUpdateDeployment(value: unknown): value is UpdateDeployment {
  return value === 'portable';
}

export const packageFileName = (): string => 'archive.zip';
export const packageProtocol = (): number => 2;
export const releaseManifestName = (): string => 'ahk-manager-portable-release.json';
export const releasePackageName = (version: string): string =>
  `AHK-Manager-${version}-Portable-x64.zip`;

export const portableManifestFile = 'release.json';
export const portableSignatureFile = 'release.json.p7s';
