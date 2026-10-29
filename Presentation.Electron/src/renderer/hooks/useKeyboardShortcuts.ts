import * as React from 'react';
import type { HostState } from '../../shared/contracts.js';
import type { Dialogs } from './useDialogs.js';
import type { LibrarySelection } from './useLibrarySelection.js';
import type { ManagerActions } from './useManagerActions.js';
import type { Operations } from './useOperations.js';

export function useKeyboardShortcuts(
  host: HostState,
  library: LibrarySelection,
  dialogs: Dialogs,
  operations: Operations,
  actions: ManagerActions,
): void {
  const busy = operations.pending || host.busy || host.administrationPending;
  const ready = host.connection === 'ready';
  const readonly = !ready || !!host.snapshot?.readOnly;

  React.useEffect(() => {
    function key(event: KeyboardEvent): void {
      if (host.administrationPending) return;
      const editing =
        event.target instanceof HTMLElement &&
        !!event.target.closest('input,textarea,select,[contenteditable="true"],[role="dialog"]');
      if (
        (event.ctrlKey || event.metaKey) &&
        event.key.toLowerCase() === 'f' &&
        library.page === 'scripts' &&
        !dialogs.open
      ) {
        event.preventDefault();
        library.search.current?.focus();
      } else if (event.key === 'F5') {
        event.preventDefault();
        if (!busy && ready) void operations.run(() => window.ahkManager.refresh());
      } else if (
        (event.ctrlKey || event.metaKey) &&
        event.key.toLowerCase() === 'n' &&
        !editing &&
        !readonly &&
        !busy
      ) {
        event.preventDefault();
        actions.add();
      } else if (event.key === 'Delete' && !editing && library.page === 'scripts') {
        event.preventDefault();
        actions.remove();
      } else if (event.key === 'Escape') library.setContext(null);
    }
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  });
}
