export type UpdateChannel = 'stable' | 'test';

export interface ParsedUpdateVersion {
  core: [number, number, number];
  test: [number, number, number] | null;
}

/** Only our published version grammar is accepted as an installer/cache identity. */
export function parseUpdateVersion(value: unknown): ParsedUpdateVersion | null {
  if (typeof value !== 'string' || value.length > 64) return null;
  const match =
    /^(0|[1-9]\d{0,9})\.(0|[1-9]\d{0,9})\.(0|[1-9]\d{0,9})(?:-test\.([1-9]\d{0,9})\.([1-9]\d{0,9})\.([1-9]\d{0,9}))?$/.exec(
      value,
    );
  if (!match || match[0] !== value) return null;
  const numbers = match
    .slice(1)
    .filter((part) => part !== undefined)
    .map(Number);
  if (numbers.some((part) => !Number.isSafeInteger(part) || part > 2147483647)) return null;
  return {
    core: [numbers[0]!, numbers[1]!, numbers[2]!],
    test: numbers.length === 6 ? [numbers[3]!, numbers[4]!, numbers[5]!] : null,
  };
}

export function compareUpdateVersions(first: string, second: string): number {
  const left = parseUpdateVersion(first),
    right = parseUpdateVersion(second);
  if (!left || !right) throw new Error('The specified version is invalid.');
  const compare = (a: number[], b: number[]): number => {
    for (let index = 0; index < a.length; index++) {
      if (a[index] !== b[index]) return a[index]! < b[index]! ? -1 : 1;
    }
    return 0;
  };
  const core = compare(left.core, right.core);
  if (core) return core;
  if (left.test === null) return right.test === null ? 0 : 1;
  return right.test === null ? -1 : compare(left.test, right.test);
}

export const validUpdateChannel = (value: unknown): value is UpdateChannel =>
  value === 'stable' || value === 'test';

export const isTestUpdateVersion = (value: string): boolean =>
  parseUpdateVersion(value)?.test != null;

export function validUpdateVersion(value: unknown): value is string {
  return parseUpdateVersion(value) !== null && compareUpdateVersions(value as string, '1.2.0') >= 0;
}

export function versionAllowedInChannel(version: string, channel: UpdateChannel): boolean {
  return validUpdateVersion(version) && (channel === 'test' || !isTestUpdateVersion(version));
}
