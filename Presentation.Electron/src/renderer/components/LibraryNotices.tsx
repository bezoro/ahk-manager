import * as React from 'react';
import { Button } from '@fluentui/react-components';
import type { HostState } from '../../shared/contracts.js';
import { reason, text } from '../resources/pt-BR.js';

interface Props {
  host: HostState;
  busy: boolean;
  onRetry(): void;
  onRecover(): void;
  onRestoreBackup(): void;
  onImport(): void;
}

export function LibraryNotices({
  host,
  busy,
  onRetry,
  onRecover,
  onRestoreBackup,
  onImport,
}: Props): React.ReactElement {
  const snapshot = host.snapshot;
  const ready = host.connection === 'ready';
  return (
    <>
      {snapshot && !ready && (
        <div className="library-notice" role="alert">
          <strong>{host.connection === 'closing' ? text.closing : text.disconnected}</strong>
          <p>{text.disconnectedHint}</p>
          {host.error && (
            <details>
              <summary>{text.technicalDetails}</summary>
              <pre>{host.error}</pre>
            </details>
          )}
          <Button size="small" disabled={busy || host.connection === 'closing'} onClick={onRetry}>
            {text.retry}
          </Button>
        </div>
      )}
      {snapshot &&
        (snapshot.readOnly || !snapshot.effectsAvailable || snapshot.issues.length > 0) && (
          <div className="library-notice">
            <details>
              <summary>
                {snapshot.readOnly
                  ? text.readonly
                  : snapshot.issues.some((issue) => issue.code === 'PendingOperation')
                    ? text.pendingRecovery
                    : reason(snapshot.issues[0]?.code ?? 'None') || text.unavailable}
              </summary>
              <ul>
                {snapshot.issues.map((issue, index) => (
                  <li key={issue.code + index}>
                    {reason(issue.code)}
                    {issue.detail && <pre className="selectable">{issue.detail}</pre>}
                  </li>
                ))}
              </ul>
            </details>
            <div className="form-actions">
              {snapshot.issues.some((issue) => issue.code === 'PendingOperation') && (
                <Button size="small" disabled={!ready || busy} onClick={onRecover}>
                  {text.recover}
                </Button>
              )}
              {snapshot.readOnly && (
                <Button size="small" disabled={!ready || busy} onClick={onRestoreBackup}>
                  {text.restoreBackup}
                </Button>
              )}
              {snapshot.issues.some(
                (issue) => issue.code === 'ImportAvailable' || issue.code === 'LegacyGuard',
              ) && (
                <Button size="small" disabled={!ready || busy} onClick={onImport}>
                  {text.importLegacy}
                </Button>
              )}
            </div>
          </div>
        )}
    </>
  );
}
