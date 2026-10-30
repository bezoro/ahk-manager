import fs from 'node:fs/promises';
import path from 'node:path';
import { load, JSON_SCHEMA } from 'js-yaml';

export async function updatePublisherNames(
  configPath = path.join(process.resourcesPath, 'app-update.yml'),
): Promise<string[]> {
  const bytes = await fs.readFile(configPath);
  if (bytes.length > 65536) throw new Error('The update signature policy is invalid.');
  const config: unknown = load(bytes.toString('utf8'), { schema: JSON_SCHEMA });
  if (!config || typeof config !== 'object' || Array.isArray(config))
    throw new Error('The update signature policy is invalid.');
  const publisher = (config as Record<string, unknown>).publisherName;
  if (publisher === undefined || publisher === null) return [];
  const names: unknown = typeof publisher === 'string' ? [publisher] : publisher;
  if (
    !Array.isArray(names) ||
    names.length < 1 ||
    names.length > 32 ||
    names.some((value) => typeof value !== 'string' || !value.trim() || value.length > 1024)
  )
    throw new Error('The update signature policy is invalid.');
  return names as string[];
}
