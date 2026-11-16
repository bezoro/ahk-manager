import type { Readable } from 'node:stream';

export const agentHelperCodes = new Set([
  'AccessDenied',
  'CancelledBeforeDispatch',
  'ElevationRequired',
  'HostBusy',
  'InternalFailure',
  'InvalidInput',
  'InvalidInstallation',
  'InvalidState',
  'NativeFailure',
  'PeerUnverified',
  'PrivateParentRequired',
  'TransportFailure',
]);
export interface AgentHelperDiagnostic {
  schemaVersion: 1;
  phase: 'server' | 'relay';
  code: string;
}

export function validAgentHelperDiagnostic(value: unknown): value is AgentHelperDiagnostic {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  return (
    Object.keys(item).length === 3 &&
    item.schemaVersion === 1 &&
    (item.phase === 'server' || item.phase === 'relay') &&
    typeof item.code === 'string' &&
    agentHelperCodes.has(item.code)
  );
}

/** The native child emits only fixed reasons after authenticating its parent/peer. */
export function readAgentHelperDiagnostic(line: string): AgentHelperDiagnostic | null {
  if (line.length > 512) return null;
  const match =
    /^AHK_AGENT_REFUSAL:\{"schemaVersion":1,"phase":"(server|relay)","code":"([A-Za-z]{1,32})"\}\r?\n$/.exec(
      line,
    );
  return match && agentHelperCodes.has(match[2]!)
    ? { schemaVersion: 1, phase: match[1] as AgentHelperDiagnostic['phase'], code: match[2]! }
    : null;
}

export class AgentTransportError extends Error {
  readonly code: string;
  constructor(readonly diagnostic: AgentHelperDiagnostic) {
    super('The authenticated agent helper reported a transport failure.');
    this.name = 'AgentTransportError';
    this.code = diagnostic.code;
  }
}

/** Bound each record; discard arbitrary or oversized stderr without retaining it. */
export function observeAgentHelperDiagnostics(
  stderr: Pick<Readable, 'on'>,
  failure: (error: Error) => void,
): () => boolean {
  const pending = Buffer.alloc(512);
  let size = 0;
  let oversized = false;
  let observed = false;
  stderr.on('data', (chunk: Buffer) => {
    if (!Buffer.isBuffer(chunk)) {
      size = 0;
      oversized = true;
      return;
    }
    for (const byte of chunk) {
      if (!oversized && size < pending.length) pending[size++] = byte;
      else oversized = true;
      if (byte !== 10) continue;
      const diagnostic = oversized
        ? null
        : readAgentHelperDiagnostic(pending.subarray(0, size).toString('utf8'));
      size = 0;
      oversized = false;
      if (diagnostic) {
        observed = true;
        try {
          failure(new AgentTransportError(diagnostic));
        } catch {
          /* Diagnostic sinks cannot change command delivery or lifecycle. */
        }
      }
    }
  });
  stderr.on('error', () => {
    size = 0;
    oversized = true;
  });
  return () => observed;
}
