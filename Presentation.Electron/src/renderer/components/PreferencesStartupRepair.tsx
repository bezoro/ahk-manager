import * as React from 'react';
import { Button } from '@fluentui/react-components';
import type { OperationResult, StartupRepairPreview } from '../../shared/contracts.js';
import { text } from '../resources/en-US.js';
import type { Notice } from './OperationNotice.js';
import { CopyableOperationNotice } from './CopyableOperationNotice.js';

/** Only a backend-proven previous registration is offered for explicit review. */
export function PreferencesStartupRepair({
  enabled,
  disabled,
  settingsChanged,
  pendingDataMove,
  storageStatus,
  refreshKey,
  savedMode,
  onPending,
  onResult,
  onRepair,
  onCopy,
}: {
  enabled: boolean;
  disabled: boolean;
  settingsChanged: boolean;
  pendingDataMove: boolean;
  storageStatus: 'loading' | 'failed' | 'ready';
  refreshKey: unknown;
  savedMode: string;
  onPending(value: boolean): void;
  onResult(result: OperationResult): void;
  onRepair(fingerprint: string): Promise<OperationResult>;
  onCopy(value: string): Promise<boolean>;
}): React.ReactElement | null {
  const [preview, setPreview] = React.useState<StartupRepairPreview | null>(null);
  const [notice, setNotice] = React.useState<Notice | null>(null);
  const [loading, setLoading] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  const mounted = React.useRef(true);
  const read = React.useRef(0);
  const mutation = React.useRef(false);

  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      read.current++;
    };
  }, []);

  const refresh = React.useCallback(async (): Promise<void> => {
    const request = ++read.current;
    setLoading(true);
    try {
      const value = await window.ahkManager.previewStartupRepair();
      if (!mounted.current || request !== read.current) return;
      if (
        value.canRepair &&
        (!value.previousExecutable || !value.currentExecutable || !value.fingerprint)
      )
        throw new Error('The startup entry review is incomplete. Check it again before applying.');
      setPreview(value);
      setNotice(null);
    } catch (error) {
      if (mounted.current && request === read.current) {
        setPreview(null);
        setNotice({
          title: text.error,
          message: text.startupReviewFailed,
          detail: error instanceof Error ? error.message : String(error),
          warning: true,
        });
      }
    } finally {
      if (mounted.current && request === read.current) setLoading(false);
    }
  }, []);

  React.useEffect(() => {
    if (enabled) void refresh();
    else {
      read.current++;
      setLoading(false);
      setPreview(null);
    }
  }, [enabled, refreshKey, savedMode, refresh]);

  async function apply(): Promise<void> {
    if (
      disabled ||
      storageStatus !== 'ready' ||
      loading ||
      mutation.current ||
      !preview?.canRepair ||
      !preview.fingerprint
    )
      return;
    const fingerprint = preview.fingerprint;
    mutation.current = true;
    setWorking(true);
    setNotice(null);
    onPending(true);
    try {
      const result = await onRepair(fingerprint);
      if (!mounted.current) return;
      if (result.outcome === 'Confirmed') setPreview(null);
      onResult(result);
      await refresh();
    } catch (error) {
      if (mounted.current) {
        setPreview(null);
        setNotice({
          title: text.error,
          message: text.startupRepairUnconfirmed,
          detail: error instanceof Error ? error.message : String(error),
          warning: true,
        });
      }
    } finally {
      mutation.current = false;
      if (mounted.current) {
        setWorking(false);
        onPending(false);
      }
    }
  }

  if (!notice && !preview?.canRepair) return null;
  return (
    <div className="startup-entry-review" aria-labelledby="startup-entry-review-title">
      <h4 id="startup-entry-review-title">
        {preview?.canRepair ? text.startupPreviousEntry : text.startupReviewTitle}
      </h4>
      <CopyableOperationNotice notice={notice} onDismiss={() => setNotice(null)} onCopy={onCopy} />
      {storageStatus !== 'ready' && (
        <p
          className="startup-storage-state muted"
          role={storageStatus === 'failed' ? 'alert' : 'status'}
        >
          {storageStatus === 'failed'
            ? text.startupRepairDataUnavailable
            : text.startupRepairCheckingData}
        </p>
      )}
      {preview?.canRepair && (
        <>
          <p>{text.startupPreviousEntryHint}</p>
          {preview.detail && <p className="muted">{preview.detail}</p>}
          <dl>
            <dt>{text.previousManager}</dt>
            <dd>
              <pre className="selectable startup-previous-executable">
                {preview.previousExecutable}
              </pre>
            </dd>
            <dt>{text.currentManager}</dt>
            <dd>
              <pre className="selectable startup-current-executable">
                {preview.currentExecutable}
              </pre>
            </dd>
          </dl>
          {settingsChanged && <p className="muted">{text.startupRepairSaveFirst}</p>}
          {pendingDataMove && <p className="muted">{text.startupRepairFinishDataMove}</p>}
          <Button
            disabled={disabled || storageStatus !== 'ready' || loading || working}
            aria-busy={working || undefined}
            onClick={() => void apply()}
          >
            {text.removePreviousStartup}
          </Button>
        </>
      )}
      {notice && (
        <Button
          disabled={!enabled || disabled || loading || working}
          onClick={() => void refresh()}
        >
          {text.reviewStartup}
        </Button>
      )}
      {(loading || working) && (
        <p role="status">{working ? text.pending : text.startupReviewPending}</p>
      )}
    </div>
  );
}
