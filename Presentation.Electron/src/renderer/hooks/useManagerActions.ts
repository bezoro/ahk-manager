import type { CatalogResult, HostState, ScriptOperation } from '../../shared/contracts.js';
import type { DeleteScope } from '../appTypes.js';
import { actionReason } from '../model.js';
import { catalogSummary, outcome, reason, text } from '../resources/pt-BR.js';
import type { Dialogs } from './useDialogs.js';
import type { LibrarySelection } from './useLibrarySelection.js';
import type { MetadataDrafts } from './useMetadataDrafts.js';
import type { Operations } from './useOperations.js';

export function useManagerActions(
  host: HostState,
  library: LibrarySelection,
  metadata: MetadataDrafts,
  dialogs: Dialogs,
  operations: Operations,
) {
  const { snapshot } = host;
  const { selected, query, setPage, changeQuery, setSelectedId, setDeleteScope } = library;
  const { drafts } = metadata;
  const { run, perform, inFlight, setNotice, onError } = operations;
  const busy = operations.pending || host.busy || host.administrationPending;
  const readonly = host.connection !== 'ready' || !!snapshot?.readOnly;

  const add = (folder = false): void => {
    void run(
      () => (folder ? window.ahkManager.addFolder() : window.ahkManager.addFiles()),
      (result: CatalogResult | null) => {
        if (!result) return;
        if (result.result.outcome !== 'CancelledBeforeDispatch')
          setNotice({
            title: outcome(result.result.outcome),
            message: [
              catalogSummary(result.added.length, result.duplicateCount),
              reason(result.result.code),
              ...result.issues.map((issue) => reason(issue.code)),
            ]
              .filter(Boolean)
              .join(' · '),
            detail:
              [result.result.detail, ...result.issues.map((issue) => issue.detail)]
                .filter(Boolean)
                .join('\n') || null,
            warning: result.result.outcome !== 'Confirmed' || result.issues.length > 0,
          });
        if (result.added[0]) {
          setPage('scripts');
          changeQuery({ search: '', status: 'All', collectionId: null });
          setSelectedId(result.added[0]);
        }
      },
    );
  };

  const action = (value: ScriptOperation): void => {
    if (!selected || !snapshot || busy) return;
    const blocked = actionReason(host, selected, value);
    if (blocked) {
      setNotice({ title: text.unavailable, message: reason(blocked), warning: true });
      return;
    }
    const selection = { scriptId: selected.id, query, stateRevision: snapshot.revision };
    window.ahkManager.updateSelection(selection);
    perform(() => window.ahkManager.operate(value, selection));
  };

  const remove = (): void => {
    if (selected && snapshot && !readonly && !busy && !drafts[selected.id]?.dirty) {
      perform(() => window.ahkManager.removeScript(selected.id, snapshot.revision));
    }
  };

  const fileAction = (kind: 'locate' | 'delete'): void => {
    if (!selected || !snapshot || readonly || busy || drafts[selected.id]?.dirty) return;
    const selection = { scriptId: selected.id, query, stateRevision: snapshot.revision };
    window.ahkManager.updateSelection(selection);
    perform(async () => {
      const scope: DeleteScope = {
        scriptId: selected.id,
        path: selected.path,
        query,
        authorityMode: host.authorityMode,
      };
      if (kind === 'delete') setDeleteScope(scope);
      try {
        return await (kind === 'locate'
          ? window.ahkManager.locateScript(selection)
          : window.ahkManager.deleteScriptFile(selection));
      } finally {
        if (kind === 'delete') setDeleteScope((current) => (current === scope ? null : current));
      }
    });
  };

  const openImport = (): void => {
    if (inFlight.current || host.busy || host.administrationPending) return;
    dialogs.setHelp(false);
    const destination = dialogs.begin('import');
    void run(
      () => window.ahkManager.previewImport(),
      (value) => {
        if (dialogs.session.current === destination) {
          if (value) dialogs.setPreview(value);
          else dialogs.close();
        }
      },
      destination,
    );
  };

  const pickExecutable = async (kind: 'editor' | 'interpreter'): Promise<string | null> => {
    const destination = dialogs.session.current;
    dialogs.setNotice(null);
    try {
      const path = await (kind === 'editor'
        ? window.ahkManager.pickEditor()
        : window.ahkManager.pickInterpreter());
      return dialogs.session.current === destination ? path : null;
    } catch (error) {
      onError(error, destination);
      return null;
    }
  };

  return { add, action, remove, fileAction, openImport, pickExecutable };
}

export type ManagerActions = ReturnType<typeof useManagerActions>;
