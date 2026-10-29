import * as React from 'react';
import type { HostState, ManagerSettings } from '../../shared/contracts.js';
import { outcome, reason, text } from '../resources/en-US.js';
import type { Dialogs } from './useDialogs.js';
import type { MetadataDrafts } from './useMetadataDrafts.js';
import type { Operations } from './useOperations.js';

export function useAdministration(
  host: HostState,
  dialogs: Dialogs,
  metadata: MetadataDrafts,
  operations: Operations,
) {
  const { inFlight, run, report, showNotice, onError } = operations;
  const { state: draftState, readbacks } = metadata;
  const guard = React.useRef({ dialogOpen: dialogs.open, pendingWork: false });
  guard.current = {
    dialogOpen: dialogs.open,
    pendingWork: host.busy || readbacks.current.size > 0,
  };

  React.useEffect(
    () =>
      window.ahkManager.onAdministrationPreflight(() => ({
        dirtyDrafts: Object.values(draftState.current).some((draft) => draft.dirty),
        dialogOpen: guard.current.dialogOpen,
        pendingWork: inFlight.current || guard.current.pendingWork,
      })),
    [draftState, inFlight],
  );

  const request = (): void => {
    void window.ahkManager
      .requestAdministration()
      .then((result) => {
        if (result.outcome === 'CancelledBeforeDispatch') {
          showNotice({
            title: outcome(result.outcome),
            message: reason(result.code),
            warning: false,
          });
        } else report(result);
      })
      .catch((error) => onError(error));
  };

  const saveSettings = (value: ManagerSettings): void => {
    const destination = dialogs.session.current;
    let needsAdministration = false;
    void run(
      () => window.ahkManager.saveSettings(value),
      (result) => {
        report(result, destination);
        if (dialogs.session.current !== destination) return;
        if (result.outcome === 'Confirmed') dialogs.close();
        else if (result.outcome === 'Partial' && result.code === 'ManualElevationRequired') {
          dialogs.close();
          needsAdministration = true;
        }
      },
      destination,
    )
      .then(async () => {
        if (!needsAdministration) return;
        // The closed dialog and completed save must leave the live preflight guard first.
        await new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        );
        request();
      })
      .catch((error) => onError(error));
  };

  const busy = operations.pending || host.busy || host.administrationPending;
  const updateBlockedReason = Object.values(metadata.drafts).some((draft) => draft.dirty)
    ? text.updateDirtyGuard
    : dialogs.open
      ? text.updateDialogGuard
      : busy || readbacks.current.size > 0
        ? text.updatePendingGuard
        : null;

  return { request, saveSettings, updateBlockedReason };
}
