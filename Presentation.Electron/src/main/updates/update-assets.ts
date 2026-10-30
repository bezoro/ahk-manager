import { assetUrl } from './github-updates.js';

const hosts = new Set(['release-assets.githubusercontent.com', 'objects.githubusercontent.com']);

/** Every asset hop stays on GitHub's fixed public API or release CDN. */
export async function openUpdateAsset(
  fetcher: typeof fetch,
  id: number,
  signal: AbortSignal,
): Promise<Response> {
  let location = new URL(assetUrl(id));
  for (let redirects = 0; redirects <= 3; redirects++) {
    if (
      location.protocol !== 'https:' ||
      location.username ||
      location.password ||
      location.port ||
      location.hash ||
      (location.origin !== 'https://api.github.com' && !hosts.has(location.hostname))
    )
      throw new Error('The update download was redirected to an untrusted location.');
    const headers: Record<string, string> = {
      Accept: 'application/octet-stream',
      'User-Agent': 'Bezoro-AhkManager-Updater',
    };
    if (location.origin === 'https://api.github.com')
      headers['X-GitHub-Api-Version'] = '2022-11-28';
    const response = await fetcher(location.href, {
      method: 'GET',
      headers,
      redirect: 'manual',
      credentials: 'omit',
      signal,
    });
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const target = response.headers.get('location');
      await response.body?.cancel();
      if (!target || redirects === 3)
        throw new Error('The update download exceeded its redirect limit.');
      const next = new URL(target, location);
      if (!hosts.has(next.hostname))
        throw new Error('The update download was redirected to an untrusted location.');
      location = next;
      continue;
    }
    if (response.status !== 200 || !response.body) {
      await response.body?.cancel();
      throw new Error(
        'The portable package could not be downloaded. Check your connection and try again.',
      );
    }
    return response;
  }
  throw new Error('The update download exceeded its redirect limit.');
}

export async function readUpdateAsset(
  fetcher: typeof fetch,
  id: number,
  maximum: number,
  signal: AbortSignal,
): Promise<Buffer> {
  const response = await openUpdateAsset(fetcher, id, signal);
  const length = response.headers.get('content-length');
  if (length !== null && (!/^\d+$/.test(length) || Number(length) > maximum)) {
    await response.body!.cancel();
    throw new Error('The portable signature exceeded its size limit.');
  }
  const reader = response.body!.getReader();
  const parts: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const value = await reader.read();
      if (value.done) break;
      total += value.value.byteLength;
      if (total > maximum) {
        await reader.cancel();
        throw new Error('The portable signature exceeded its size limit.');
      }
      parts.push(value.value);
    }
  } finally {
    reader.releaseLock();
  }
  if (total < 1) throw new Error('The portable signature is empty.');
  return Buffer.concat(parts);
}
