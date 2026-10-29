import type { ClientRequest, ClientRequestConstructorOptions, Session } from 'electron';
import { Readable } from 'node:stream';

export type ElectronRequestFactory = (options: ClientRequestConstructorOptions) => ClientRequest;

function responseHeaders(source: Record<string, string | string[]>): Headers {
  const headers = new Headers();
  for (const [name, value] of Object.entries(source))
    for (const item of Array.isArray(value) ? value : [value]) headers.append(name, item);
  return headers;
}

/** Electron fetch cancels manual redirects. Expose each hop for the client's validation. */
export function createElectronUpdateFetch(
  network: Session,
  requestFactory: ElectronRequestFactory,
): typeof fetch {
  return async (input, init) => {
    const request = new Request(input, init);
    if (request.method !== 'GET' || request.redirect !== 'manual')
      throw new Error('Update transport accepts only GET requests with manual redirects.');
    if (request.signal.aborted) throw request.signal.reason;
    return new Promise<Response>((resolve, reject) => {
      let outgoing: ClientRequest | undefined;
      let incoming: Readable | undefined;
      let settled = false;
      let ended = false;
      const cleanup = () => request.signal.removeEventListener('abort', abort);
      const fail = (error: Error) => {
        cleanup();
        if (!settled) {
          settled = true;
          reject(error);
        }
        incoming?.destroy(error);
        outgoing?.abort();
      };
      const abort = () =>
        fail(
          request.signal.reason instanceof Error
            ? request.signal.reason
            : new DOMException('The update request was aborted.', 'AbortError'),
        );
      try {
        outgoing = requestFactory({
          url: request.url,
          session: network,
          method: 'GET',
          redirect: 'manual',
          credentials: 'omit',
          useSessionCookies: false,
        });
        outgoing.on('error', fail);
        outgoing.on('abort', () => {
          if (!settled) fail(new DOMException('The update request was aborted.', 'AbortError'));
        });
        outgoing.on('redirect', (status, _method, target, source) => {
          if (settled) return;
          try {
            const headers = responseHeaders(source);
            headers.set('location', target);
            const response = new Response(null, { status, headers });
            settled = true;
            cleanup();
            resolve(response);
            // Synchronous abort prevents Electron from following the hop or
            // rejecting it as an unhandled manual redirect.
            outgoing!.abort();
          } catch (error) {
            fail(error instanceof Error ? error : new Error('Invalid update redirect response.'));
          }
        });
        outgoing.on('response', (response) => {
          if (settled) return;
          incoming = response as unknown as Readable;
          incoming.on('error', (error: Error) => {
            cleanup();
            if (!settled) fail(error);
          });
          incoming.once('end', () => {
            ended = true;
            cleanup();
          });
          incoming.once('close', () => {
            cleanup();
            if (!ended) outgoing!.abort();
          });
          try {
            const empty = [204, 205, 304].includes(response.statusCode);
            const body = empty
              ? null
              : (Readable.toWeb(incoming, {
                  strategy: {
                    highWaterMark: 64 * 1024,
                    size: (chunk: Uint8Array) => chunk.byteLength,
                  },
                }) as ReadableStream<Uint8Array>);
            const result = new Response(body, {
              status: response.statusCode,
              statusText: response.statusMessage,
              headers: responseHeaders(response.headers),
            });
            settled = true;
            resolve(result);
            if (empty) incoming.resume();
          } catch (error) {
            fail(error instanceof Error ? error : new Error('Invalid update response.'));
          }
        });
        for (const [name, value] of request.headers) outgoing.setHeader(name, value);
        request.signal.addEventListener('abort', abort, { once: true });
        if (request.signal.aborted) abort();
        else outgoing.end();
      } catch (error) {
        fail(error instanceof Error ? error : new Error('The update request failed.'));
      }
    });
  };
}
