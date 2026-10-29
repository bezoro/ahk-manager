import * as React from 'react';
import { Button, Spinner } from '@fluentui/react-components';
import type { HostState } from '../../shared/contracts.js';
import { text } from '../resources/en-US.js';

export function DisconnectedPage({
  host,
  busy,
  onRetry,
}: {
  host: HostState;
  busy: boolean;
  onRetry(): void;
}): React.ReactElement {
  return (
    <div className="empty-page">
      {host.connection === 'starting' ? (
        <Spinner label={text.starting} />
      ) : (
        <>
          <h2>{host.connection === 'closing' ? text.closing : text.disconnected}</h2>
          <p>{text.disconnectedHint}</p>
          {host.error && (
            <details>
              <summary>{text.technicalDetails}</summary>
              <pre>{host.error}</pre>
            </details>
          )}
          <Button
            appearance="primary"
            disabled={busy || host.connection === 'closing'}
            onClick={onRetry}
          >
            {text.retry}
          </Button>
        </>
      )}
    </div>
  );
}
