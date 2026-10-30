import * as React from 'react';
import type { StorageState } from '../../shared/storage.js';

/** Keeps storage actions separate from the editable application preferences. */
export function useDataLocation(closePreferences: () => void) {
  const [state, setState] = React.useState<StorageState | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const running = React.useRef(false);
  const mounted = React.useRef(true);

  React.useEffect(() => {
    mounted.current = true;
    void window.ahkManager.getStorageState().then(
      (value) => {
        if (mounted.current) setState(value);
      },
      (failure: unknown) => {
        if (mounted.current)
          setError(
            failure instanceof Error ? failure.message : 'The data location could not be loaded.',
          );
      },
    );
    return () => {
      mounted.current = false;
    };
  }, []);

  async function run(action: () => Promise<void>): Promise<void> {
    if (running.current) return;
    running.current = true;
    setBusy(true);
    try {
      await action();
    } finally {
      running.current = false;
      if (mounted.current) setBusy(false);
    }
  }

  return {
    state,
    error,
    busy,
    choose: () =>
      run(async () => {
        const result = await window.ahkManager.chooseDataLocation();
        if (result && mounted.current) setState(result.state);
      }),
    open: () => run(() => window.ahkManager.openDataLocation()),
    useDefault: () =>
      run(async () => {
        const result = await window.ahkManager.restoreDefaultDataLocation();
        if (mounted.current) setState(result.state);
      }),
    cancelMove: () =>
      run(async () => {
        const result = await window.ahkManager.cancelDataMove();
        if (mounted.current) setState(result);
      }),
    restart: () =>
      run(async () => {
        closePreferences();
        // Publish the closed dialog to the authenticated preflight provider first.
        await new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        );
        await window.ahkManager.restartForDataMove();
      }),
  };
}
