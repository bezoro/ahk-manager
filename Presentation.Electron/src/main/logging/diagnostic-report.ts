import type { HostState } from '../../shared/contracts.js';
import type { UpdateState } from '../../shared/updates.js';
import { parseUpdateVersion } from '../../shared/update-versions.js';
import { guid, operation, outcome, reason } from '../../shared/validation.js';
import type { CommandLog, DiagnosticLogMode } from './command-log.js';

export interface DiagnosticReportContext {
  version: string;
  platform: string;
  architecture: string;
  operatingSystemRelease: string;
  deployment: 'portable' | 'installed';
  host: HostState | null;
  updates: UpdateState | null;
}

const count = (value: number): number => Math.max(0, Math.min(1000000, Math.floor(value)));
const version = (value: unknown): string | null =>
  parseUpdateVersion(value) ? (value as string) : null;
const timestamp = (value: unknown): string | null =>
  typeof value === 'string' &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(value) &&
  Number.isFinite(Date.parse(value))
    ? new Date(value).toISOString()
    : null;
const member = (value: unknown, allowed: readonly string[]): string | null =>
  typeof value === 'string' && allowed.includes(value) ? value : null;
const boolean = (value: unknown): boolean | null => (typeof value === 'boolean' ? value : null);

/** A bounded report for pasting into support chats. Raw diagnostic payloads stay local. */
export function createDiagnosticReport(
  context: DiagnosticReportContext,
  log: Pick<CommandLog, 'snapshot' | 'status'> | null,
  mode: DiagnosticLogMode,
): string {
  let records: Readonly<Record<string, string | number | boolean | null>>[] = [];
  let omittedRecords = 0;
  let skippedFiles = 0;
  let readStatus = log ? 'unavailable' : 'not-configured';
  if (log) {
    try {
      const snapshot = log.snapshot(mode);
      records = snapshot.records;
      omittedRecords = snapshot.omittedRecords;
      skippedFiles = snapshot.skippedFiles;
      readStatus = 'completed';
    } catch {
      // A report with host state is still useful when the log directory cannot be read.
    }
  }
  const host = context.host;
  const profile = host?.snapshot;
  const updates = context.updates;
  const failures = new Set(['Failed', 'Partial', 'Unknown']);
  const activity = (profile?.activity ?? [])
    .filter((entry) => mode === 'recent' || failures.has(entry.outcome))
    .slice(0, 20)
    .map((entry) => ({
      timestamp: timestamp(entry.timestamp),
      operation: operation(entry.operation) ? entry.operation : null,
      outcome: outcome(entry.outcome) ? entry.outcome : 'Unknown',
      code: reason(entry.code) ? entry.code : 'Unknown',
      scriptId: guid(entry.scriptId) ? entry.scriptId : null,
    }));
  const status = log?.status();
  const summary = {
    formatVersion: 1,
    collectedAt: new Date().toISOString(),
    mode,
    application: {
      name: 'AHK Manager',
      version: version(context.version),
      deployment: member(context.deployment, ['portable', 'installed']),
      platform: member(context.platform, ['win32', 'linux', 'darwin']),
      architecture: member(context.architecture, ['x64', 'arm64', 'ia32']),
      operatingSystemRelease: /^\d{1,6}(?:\.\d{1,10}){0,4}$/.test(context.operatingSystemRelease)
        ? context.operatingSystemRelease
        : null,
    },
    backend: {
      connection: member(host?.connection, ['starting', 'ready', 'failed', 'closing']),
      authority: member(host?.authorityMode, ['Normal', 'Administrator']),
      busy: host?.busy === true,
      administrationPending: host?.administrationPending === true,
      profileLoaded: !!profile,
      readOnly: boolean(profile?.readOnly),
      effectsAvailable: boolean(profile?.effectsAvailable),
      scripts: count(profile?.scripts.length ?? 0),
      runningScripts: count(
        profile?.scripts.filter((script) => script.status === 'Running').length ?? 0,
      ),
      issues: (profile?.issues ?? []).slice(0, 20).map((issue) => ({
        code: reason(issue.code) ? issue.code : 'Unknown',
        scriptId: guid(issue.scriptId) ? issue.scriptId : null,
      })),
    },
    startup: profile
      ? {
          launchManagerAtLogin: boolean(profile.settings.launchManagerAtLogin),
          launchManagerAsAdministrator: boolean(profile.settings.launchManagerAsAdministrator),
          startInTray: boolean(profile.settings.startInTray),
          showWindowAtLogin: boolean(profile.settings.showWindowAtLogin),
        }
      : null,
    updates: updates
      ? {
          channel: member(updates.channel, ['stable', 'test']),
          phase: member(updates.phase, ['idle', 'checking', 'downloading', 'installing', 'error']),
          availableVersion: version(updates.availableVersion),
          lastCheckedAt: timestamp(updates.lastCheckedAt),
        }
      : null,
    logging: {
      available: status?.available === true,
      failure: member(status?.failure, ['unavailable', 'unsafe-path', 'retention-limit', 'closed']),
      readStatus,
      skippedFiles: count(skippedFiles),
    },
    recentActivity: activity,
  };
  const prefix =
    [
      'AHK Manager diagnostic report',
      'Times are UTC. This report covers bounded recent local history, not every past session.',
      'File paths, names, tokens, script contents and raw error messages are excluded.',
      JSON.stringify(summary, null, 2),
      '',
      mode === 'errors'
        ? 'Recent failures and refusals, with command context (JSON Lines):'
        : 'Recent commands (JSON Lines):',
    ].join('\n') + '\n';
  const selected: string[] = [];
  let bytes = Buffer.byteLength(prefix, 'utf8') + 256;
  for (let index = records.length - 1; index >= 0; index--) {
    const line = JSON.stringify(records[index]) + '\n';
    const size = Buffer.byteLength(line, 'utf8');
    if (bytes + size > 65536) {
      omittedRecords += index + 1;
      break;
    }
    selected.push(line);
    bytes += size;
  }
  return (
    prefix +
    selected.reverse().join('') +
    [
      selected.length
        ? ''
        : readStatus === 'completed'
          ? 'No matching commands in the readable recent history.\n'
          : 'Command logs could not be read.\n',
      `Included records: ${selected.length}. Older or excess matching records omitted: ${count(omittedRecords)}. Files skipped: ${count(skippedFiles)}.\n`,
    ].join('')
  );
}
