import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ClientRequest, ClientRequestConstructorOptions, Session } from 'electron';
import { createElectronUpdateFetch } from '../../src/main/electron-update-fetch.js';

class RequestFixture extends EventEmitter {
  aborted = false;
  ended = false;
  followed = false;
  headers = new Headers();
  constructor(private readonly respond: (request: RequestFixture) => void) {
    super();
  }
  setHeader(name: string, value: string): void {
    this.headers.set(name, value);
  }
  end(): void {
    this.ended = true;
    queueMicrotask(() => {
      if (!this.aborted) this.respond(this);
    });
  }
  abort(): void {
    if (this.aborted) return;
    this.aborted = true;
    queueMicrotask(() => this.emit('abort'));
  }
  followRedirect(): void {
    this.followed = true;
  }
}

class ResponseFixture extends PassThrough {
  statusCode = 200;
  statusMessage = 'OK';
  headers: Record<string, string | string[]> = { 'content-type': 'application/json' };
}

function fixture(respond: (request: RequestFixture) => void = () => {}) {
  const requests: RequestFixture[] = [];
  const options: ClientRequestConstructorOptions[] = [];
  const network = {} as Session;
  const fetcher = createElectronUpdateFetch(network, (value) => {
    options.push(value);
    const request = new RequestFixture(respond);
    requests.push(request);
    return request as unknown as ClientRequest;
  });
  return { fetcher, requests, options, network };
}

const input = 'https://api.github.com/repos/bezoro/ahk-manager/releases';
const manual = { redirect: 'manual' } as const;

test('manual redirects return their target before Electron cancels them and never follow the hop', async () => {
  const location = 'https://release-assets.githubusercontent.com/asset?signature=fixture';
  const f = fixture((request) => {
    request.emit('redirect', 302, 'GET', location, { location: [location] });
    assert.equal(request.aborted, true, 'abort must be synchronous inside the redirect event');
    assert.equal(request.followed, false);
  });
  const response = await f.fetcher(input, manual);
  assert.equal(response.status, 302);
  assert.equal(response.headers.get('location'), location);
  assert.equal(response.body, null);
  assert.equal(f.requests.length, 1);
});

test('requests keep the dedicated session, omit credentials and preserve conditional API headers', async () => {
  const f = fixture((request) => {
    const response = new ResponseFixture();
    request.emit('response', response);
    response.end('{"ok":true}');
  });
  const response = await f.fetcher(input, {
    ...manual,
    headers: { Accept: 'application/vnd.github+json', 'If-None-Match': 'fixture-etag' },
  });
  assert.deepEqual(await response.json(), { ok: true });
  assert.equal(f.options[0]!.session, f.network);
  assert.equal(f.options[0]!.credentials, 'omit');
  assert.equal(f.options[0]!.useSessionCookies, false);
  assert.equal(f.options[0]!.redirect, 'manual');
  assert.equal(f.requests[0]!.headers.get('if-none-match'), 'fixture-etag');
});

test('304 responses have no body and preserve ETags for the update client cache', async () => {
  const f = fixture((request) => {
    const response = new ResponseFixture();
    response.statusCode = 304;
    response.statusMessage = 'Not Modified';
    response.headers = { etag: 'same-release-list' };
    request.emit('response', response);
    response.end();
  });
  const response = await f.fetcher(input, manual);
  assert.equal(response.status, 304);
  assert.equal(response.body, null);
  assert.equal(response.headers.get('etag'), 'same-release-list');
});

test('an already aborted signal does not create a request', async () => {
  const f = fixture();
  await assert.rejects(f.fetcher(input, { ...manual, signal: AbortSignal.abort() }), {
    name: 'AbortError',
  });
  assert.equal(f.requests.length, 0);
});

test('aborting before response headers rejects and cancels the Chromium request', async () => {
  const controller = new AbortController();
  const f = fixture();
  const pending = f.fetcher(input, { ...manual, signal: controller.signal });
  controller.abort();
  await assert.rejects(pending, { name: 'AbortError' });
  assert.equal(f.requests[0]!.aborted, true);
});

test('aborting while reading a body rejects the reader and cancels the Chromium request', async () => {
  const controller = new AbortController();
  const f = fixture((request) => request.emit('response', new ResponseFixture()));
  const response = await f.fetcher(input, { ...manual, signal: controller.signal });
  const reading = response.text();
  controller.abort();
  await assert.rejects(reading, { name: 'AbortError' });
  assert.equal(f.requests[0]!.aborted, true);
});

test('cancelling an oversized response body cancels its network request', async () => {
  const f = fixture((request) => request.emit('response', new ResponseFixture()));
  const response = await f.fetcher(input, manual);
  await response.body!.cancel();
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(f.requests[0]!.aborted, true);
});

test('network errors reject before headers and remain observable during body reads', async () => {
  const before = fixture((request) =>
    request.emit('error', new Error('net::ERR_CONNECTION_RESET')),
  );
  await assert.rejects(before.fetcher(input, manual), /ERR_CONNECTION_RESET/);
  assert.equal(before.requests[0]!.aborted, true);
  let incoming!: ResponseFixture;
  const after = fixture((request) => {
    incoming = new ResponseFixture();
    request.emit('response', incoming);
  });
  const response = await after.fetcher(input, manual);
  const reading = response.text();
  incoming.destroy(new Error('incomplete response'));
  await assert.rejects(reading, /incomplete response/);
  assert.equal(after.requests[0]!.aborted, true);
});

test('unsupported methods and automatic redirects cannot reach Chromium', async () => {
  const f = fixture();
  for (const init of [{ method: 'POST', ...manual }, { redirect: 'follow' as const }])
    await assert.rejects(f.fetcher(input, init), /only GET requests with manual redirects/);
  assert.equal(f.requests.length, 0);
});

test('an incomplete body close rejects the reader and aborts the underlying request', async () => {
  let incoming!: ResponseFixture;
  const f = fixture((request) => {
    incoming = new ResponseFixture();
    request.emit('response', incoming);
  });
  const response = await f.fetcher(input, manual);
  const reading = response.text();
  incoming.write('partial response');
  incoming.destroy();
  await assert.rejects(reading);
  assert.equal(f.requests[0]!.aborted, true);
});

test('late request errors cannot replace a settled redirect response', async () => {
  const f = fixture((request) => request.emit('redirect', 302, 'GET', input, {}));
  const response = await f.fetcher(input, manual);
  assert.doesNotThrow(() => f.requests[0]!.emit('error', new Error('late network cancellation')));
  assert.equal(response.status, 302);
  assert.equal(response.headers.get('location'), input);
});

test('invalid redirect responses reject without starting another request', async () => {
  const f = fixture((request) => request.emit('redirect', 999, 'GET', input, {}));
  await assert.rejects(f.fetcher(input, manual));
  assert.equal(f.requests.length, 1);
  assert.equal(f.requests[0]!.aborted, true);
});
