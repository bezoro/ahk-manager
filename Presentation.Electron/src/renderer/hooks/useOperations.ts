import * as React from 'react';
import type { HostState, OperationResult } from '../../shared/contracts.js';
import type { DialogSession } from '../appTypes.js';
import type { Notice } from '../components/OperationNotice.js';
import { outcome, reason, text } from '../resources/pt-BR.js';
import type { Dialogs } from './useDialogs.js';

export function useOperations(host: HostState, dialogs: Dialogs) {
  const [notice, setNotice] = React.useState<Notice | null>(null);
  const [pending, setPending] = React.useState(false);
  const inFlight = React.useRef(false);
  const { session, setNotice: setDialogNotice, close: closeDialog } = dialogs;

  const showNotice = React.useCallback(
    (value: Notice, destination: DialogSession | null = null): void => {
      setNotice(value);
      if (destination && session.current === destination) setDialogNotice(value);
    },
    [session, setDialogNotice],
  );

  const onError = React.useCallback(
    (error: unknown, destination: DialogSession | null = null): void => {
      showNotice(
        {
          title: text.error,
          message: text.unknownError,
          detail: error instanceof Error ? error.message : String(error),
          warning: true,
        },
        destination,
      );
    },
    [showNotice],
  );

  const report = React.useCallback(
    (result: OperationResult, destination: DialogSession | null = null): void => {
      if (result.outcome === 'CancelledBeforeDispatch') return;
      showNotice(
        {
          title: outcome(result.outcome),
          message: reason(result.code),
          detail: result.detail,
          warning: result.outcome !== 'Confirmed',
        },
        destination,
      );
    },
    [showNotice],
  );

  const run = React.useCallback(
    async <T>(
      task: () => Promise<T>,
      complete?: (value: T) => void,
      destination: DialogSession | null = null,
    ): Promise<void> => {
      if (inFlight.current || host.busy || host.administrationPending) return;
      if (destination && session.current === destination) setDialogNotice(null);
      inFlight.current = true;
      setPending(true);
      try {
        const value = await task();
        complete?.(value);
      } catch (error) {
        onError(error, destination);
      } finally {
        inFlight.current = false;
        setPending(false);
      }
    },
    [onError, host.busy, host.administrationPending, session, setDialogNotice],
  );

  const perform = (
    task: () => Promise<OperationResult | null>,
    complete?: () => void,
    destination: DialogSession | null = null,
  ): void => {
    void run(
      task,
      (result) => {
        if (!result) return;
        report(result, destination);
        if (result.outcome === 'Confirmed') complete?.();
      },
      destination,
    );
  };

  const performDialog = (
    task: () => Promise<OperationResult | null>,
    closeOnSuccess = true,
  ): void => {
    const destination = session.current;
    perform(
      task,
      () => {
        if (closeOnSuccess && session.current === destination) closeDialog();
      },
      destination,
    );
  };

  const copy = (value: string): void => {
    void window.ahkManager
      .copyText(value)
      .then(() => setNotice({ title: text.copied, message: '', warning: false }))
      .catch((error) => onError(error));
  };

  return {
    notice,
    setNotice,
    pending,
    inFlight,
    showNotice,
    onError,
    report,
    run,
    perform,
    performDialog,
    copy,
  };
}

export type Operations = ReturnType<typeof useOperations>;
