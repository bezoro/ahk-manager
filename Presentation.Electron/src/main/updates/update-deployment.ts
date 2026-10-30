export type UpdateDeployment = 'installed' | 'portable';

export function validUpdateDeployment(value: unknown): value is UpdateDeployment {
  return value === 'installed' || value === 'portable';
}

export const packageFileName = (deployment: UpdateDeployment): string =>
  deployment === 'portable' ? 'archive.zip' : 'installer.exe';

export const packageProtocol = (deployment: UpdateDeployment): number =>
  deployment === 'portable' ? 2 : 1;

export const releaseManifestName = (deployment: UpdateDeployment): string =>
  deployment === 'portable' ? 'ahk-manager-portable-release.json' : 'ahk-manager-release.json';

export const releasePackageName = (version: string, deployment: UpdateDeployment): string =>
  `AHK-Manager-${version}-${deployment === 'portable' ? 'Portable-x64.zip' : 'Setup-x64.exe'}`;

export const portableManifestFile = 'release.json';
export const portableSignatureFile = 'release.json.p7s';
