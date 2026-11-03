import * as React from 'react';
import { text } from '../resources/en-US.js';
import { OperationNotice, type Notice } from './OperationNotice.js';

/** Clipboard feedback belongs to the displayed diagnostic, including when a dialog closes. */
export function CopyableOperationNotice({
  notice,
  onDismiss,
  onCopy,
}: {
  notice: Notice | null;
  onDismiss(): void;
  onCopy(value: string): Promise<boolean>;
}): React.ReactElement {
  const [feedback, setFeedback] = React.useState<{
    notice: Notice;
    status: 'copied' | 'failed';
  } | null>(null);
  const [pending, setPending] = React.useState(false);
  const request = React.useRef(false);
  const mounted = React.useRef(true);
  const currentNotice = React.useRef(notice);
  currentNotice.current = notice;
  React.useEffect(() => {
    mounted.current = true;
    currentNotice.current = notice;
    return () => {
      mounted.current = false;
      currentNotice.current = null;
    };
  }, []);
  const status = notice
    ? pending
      ? 'pending'
      : feedback?.notice === notice
        ? feedback.status
        : null
    : null;

  async function copy(value: string): Promise<void> {
    if (!notice || request.current) return;
    const source = notice;
    request.current = true;
    setPending(true);
    setFeedback(null);
    try {
      const copied = await onCopy(value);
      if (currentNotice.current === source)
        setFeedback({ notice: source, status: copied ? 'copied' : 'failed' });
    } catch {
      if (currentNotice.current === source) setFeedback({ notice: source, status: 'failed' });
    } finally {
      request.current = false;
      if (mounted.current) setPending(false);
    }
  }

  return (
    <>
      <OperationNotice
        notice={notice}
        onDismiss={onDismiss}
        onCopy={(value) => void copy(value)}
        copyPending={pending}
      />
      {status && (
        <p className="copy-feedback muted" role={status === 'failed' ? 'alert' : 'status'}>
          {status === 'pending'
            ? text.pending
            : status === 'copied'
              ? text.copied
              : text.copyDetailsFailed}
        </p>
      )}
    </>
  );
}
