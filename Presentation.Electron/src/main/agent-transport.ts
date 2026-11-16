import type { Readable, Writable } from 'node:stream';
import { AgentTransportError, observeAgentHelperDiagnostics } from './agent-helper-diagnostics.js';
import { parseStorageJson } from './storage/json.js';

import { spawn } from 'node:child_process';
import { TextDecoder } from 'node:util';
import { bool, guid, object, shape, text } from '../shared/validation.js';

export const MAX_AGENT_REQUEST = 1024 * 1024;
export const MAX_AGENT_RESPONSE = 8 * 1024 * 1024;
const MAX_IN_FLIGHT = 8;
const ACK_DEADLINE_MS = 5_000;
const MAX_EXPIRED_RECEIPTS = 64;

export interface AgentRequest {
  protocol: 1;
  requestId: string;
  command: string;
  arguments?: Record<string, unknown>;
}

export type AgentOutcome = 'Confirmed' | 'Accepted' | 'Partial' | 'Failed' | 'Unknown';
export interface AgentReply {
  outcome: AgentOutcome;
  code: string;
  result: unknown;
  afterReply?(delivered: boolean): void | Promise<void>;
}

interface AgentAck {
  protocol: 1;
  requestId: string;
  delivered: boolean;
}

const versionOne = (value: unknown) => value === 1;
const request = shape(
  { protocol: versionOne, requestId: guid, command: text(100) },
  { arguments: object },
);
const ack = shape({ protocol: versionOne, requestId: guid, delivered: bool });
const cancel = shape({
  protocol: versionOne,
  requestId: guid,
  canceled: (value) => value === true,
});
interface Tracking {
  canceled: boolean;
  acknowledge?: (delivered: boolean) => void;
}

export class AgentFrameReader {
  private size = 0;
  private payload: Buffer | null = null;
  private readonly header = Buffer.alloc(4);
  private readonly utf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

  push(chunk: Buffer, receive: (value: unknown) => void): void {
    let offset = 0;
    while (offset < chunk.length) {
      const target = this.payload ?? this.header;
      const count = chunk.copy(target, this.size, offset, offset + target.length - this.size);
      this.size += count;
      offset += count;
      if (this.size < target.length) continue;
      this.size = 0;
      if (this.payload) {
        this.payload = null;
        receive(parseStorageJson(this.utf8.decode(target)));
        continue;
      }
      const length = this.header.readUInt32LE();
      if (length === 0 || length > MAX_AGENT_REQUEST) throw new Error('Invalid agent frame size.');
      this.payload = Buffer.allocUnsafe(length);
    }
  }
}

export function agentFrame(value: unknown): Buffer {
  const payload = Buffer.from(JSON.stringify(value), 'utf8');
  if (payload.byteLength > MAX_AGENT_RESPONSE)
    throw new Error('Agent reply exceeds its size limit.');
  const frame = Buffer.allocUnsafe(payload.length + 4);
  frame.writeUInt32LE(payload.length);
  payload.copy(frame, 4);
  return frame;
}

type AgentHostIdentity = { version: string; generation: string };
type Dispatch = (value: AgentRequest, alive: () => boolean) => Promise<AgentReply>;
type Failure = (error: unknown) => void;

export function serveAgentTransport(
  input: Readable,
  output: Writable,
  host: AgentHostIdentity,
  dispatch: Dispatch,
  terminate: () => void,
  failure: Failure,
): (error?: unknown) => void {
  const frames = new AgentFrameReader();
  const tracking = new Map<string, Tracking>();
  const expiredReceipts = new Map<string, 'waiting' | 'consumed'>();
  let inFlight = 0;
  let closed = false;

  function disconnect(error?: unknown): void {
    if (closed) return;
    closed = true;
    for (const pending of tracking.values()) pending.acknowledge?.(false);
    tracking.clear();
    expiredReceipts.clear();
    input.pause();
    output.destroy();
    terminate();
    if (error) failure(error);
  }

  async function respond(value: AgentRequest): Promise<void> {
    const key = value.requestId;
    if (tracking.has(key) || expiredReceipts.has(key) || inFlight >= MAX_IN_FLIGHT)
      throw new Error('Invalid agent concurrency.');
    const pending: Tracking = { canceled: false };
    tracking.set(key, pending);
    inFlight++;
    let reply: AgentReply;
    try {
      reply = await dispatch(
        value,
        () => !closed && !pending.canceled && tracking.get(key) === pending,
      );
    } catch (error) {
      failure(error);
      reply = { outcome: 'Unknown', code: 'CommandUnconfirmed', result: null };
    }
    if (closed) {
      await reply.afterReply?.(false);
      return;
    }
    const identity = { protocol: 1, requestId: key, ...host };
    let frame: Buffer;
    try {
      const { outcome, code, result } = reply;
      frame = agentFrame({ ...identity, outcome, code, result: result ?? null });
    } catch {
      await reply.afterReply?.(false);
      reply = { outcome: 'Unknown', code: 'ReplyUnavailable', result: null };
      frame = agentFrame({ ...identity, ...reply });
    }
    const receipt = new Promise<boolean>((resolve) => {
      const settle = (delivered: boolean) => {
        // Release admission before the frame reader handles the next frame in this chunk.
        // Keep the identity until afterReply starts so its liveness check still works.
        pending.acknowledge = undefined;
        inFlight--;
        clearTimeout(timer);
        resolve(delivered);
      };
      const timer = setTimeout(() => {
        // The native relay can still be finishing its own receipt wait.
        if (expiredReceipts.size >= MAX_EXPIRED_RECEIPTS)
          expiredReceipts.delete(expiredReceipts.keys().next().value!);
        expiredReceipts.set(key, 'waiting');
        settle(false);
      }, ACK_DEADLINE_MS);
      pending.acknowledge = settle;
    });
    output.write(frame);
    const delivered = await receipt;
    const completion = reply.afterReply?.(delivered && !closed && !pending.canceled);
    tracking.delete(key);
    await completion;
  }

  input.on('data', (chunk: Buffer) => {
    if (closed) return;
    try {
      frames.push(chunk, (value) => {
        if (cancel(value)) {
          const pending = tracking.get((value as { requestId: string }).requestId);
          if (pending) pending.canceled = true;
          return;
        }
        if (ack(value)) {
          const message = value as AgentAck;
          if (expiredReceipts.get(message.requestId) === 'waiting') {
            expiredReceipts.set(message.requestId, 'consumed');
            return;
          }
          const pending = tracking.get(message.requestId);
          if (!pending?.acknowledge) throw new Error('Unexpected agent receipt.');
          pending.acknowledge(message.delivered);
          return;
        }
        if (!request(value)) throw new Error('Invalid agent request.');
        void respond(value as AgentRequest).catch(disconnect);
      });
    } catch (error) {
      disconnect(error);
    }
  });
  input.on('error', disconnect);
  output.on('error', disconnect);
  input.on('end', () => disconnect());
  return disconnect;
}

export function startAgentTransport(
  executable: string,
  host: AgentHostIdentity,
  dispatch: Dispatch,
  failure: Failure,
): () => Promise<void> {
  const child = spawn(executable, ['--server'], { stdio: 'pipe', windowsHide: true });
  // Diagnostics never share the private command protocol or expose raw stderr.
  let closing = false;
  let disconnecting = false;
  let terminalFailure = false;
  const report = (error: unknown) => {
    if (disconnecting) terminalFailure = true;
    failure(error);
  };
  observeAgentHelperDiagnostics(child.stderr, (error) => {
    if (closing) return;
    if (error instanceof AgentTransportError && error.diagnostic.phase === 'server')
      terminalFailure = true;
    report(error);
  });
  const disconnect = serveAgentTransport(
    child.stdout,
    child.stdin,
    host,
    dispatch,
    () => {
      disconnecting = true;
      if (child.exitCode === null) child.kill();
    },
    report,
  );
  const exited = new Promise<void>((resolve) =>
    child.once('close', () => {
      if (!closing && !terminalFailure)
        report(
          new AgentTransportError({ schemaVersion: 1, phase: 'server', code: 'TransportFailure' }),
        );
      disconnect();
      resolve();
    }),
  );
  child.on('error', disconnect);
  return async () => {
    closing = true;
    disconnect();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        exited,
        new Promise<never>((_resolve, reject) => {
          timer = setTimeout(
            () => reject(new Error('Agent transport did not exit in time.')),
            ACK_DEADLINE_MS,
          );
        }),
      ]);
    } finally {
      clearTimeout(timer);
    }
  };
}
