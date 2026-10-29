import * as React from 'react';
import type { HostState, ScriptQuery } from '../../shared/contracts.js';
import type { DeleteScope, Page, ScriptContext } from '../appTypes.js';
import { defaultQuery, nextQuery, visibleScripts } from '../model.js';

export function useLibrarySelection(host: HostState, narrow: boolean) {
  const snapshot = host.snapshot;
  const ready = host.connection === 'ready';
  const [page, setPage] = React.useState<Page>('scripts');
  const [query, setQuery] = React.useState(defaultQuery);
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  const [deleteScope, setDeleteScope] = React.useState<DeleteScope | null>(null);
  const [showDetails, setShowDetails] = React.useState(false);
  const [context, setContext] = React.useState<ScriptContext | null>(null);
  const search = React.useRef<HTMLInputElement>(null);
  const inspector = React.useRef<HTMLElement>(null);
  const contextRef = React.useRef<HTMLDivElement>(null);
  const rows = React.useMemo(
    () => (snapshot ? visibleScripts(snapshot, query) : []),
    [snapshot, query],
  );

  const deleteScopeMatches =
    !!deleteScope &&
    page === 'scripts' &&
    selectedId === deleteScope.scriptId &&
    query === deleteScope.query &&
    ready &&
    host.authorityMode === deleteScope.authorityMode &&
    !host.administrationPending &&
    !!snapshot &&
    !snapshot.readOnly &&
    snapshot.effectsAvailable;
  const deleteCandidate = deleteScopeMatches
    ? snapshot?.scripts.find((script) => script.id === deleteScope.scriptId)
    : null;
  const retainedDelete =
    deleteCandidate &&
    deleteCandidate.path.toLowerCase() === deleteScope!.path.toLowerCase() &&
    !deleteCandidate.process &&
    (deleteCandidate.status === 'Stopped' ||
      (deleteCandidate.status === 'Unknown' &&
        deleteCandidate.issues.some((issue) => issue.code === 'PendingOperation'))) &&
    !deleteCandidate.issues.some((issue) =>
      [
        'ObservationIncomplete',
        'IdentityChanged',
        'MultipleInstances',
        'AccessDenied',
        'EffectOwnerUnavailable',
        'StorageFailure',
        'MissingFile',
        'LegacyGuard',
      ].includes(issue.code),
    )
      ? deleteCandidate
      : null;
  const selected =
    page === 'scripts'
      ? (rows.find((script) => script.id === selectedId) ?? retainedDelete ?? null)
      : null;
  const selectionKey = selected?.id ?? null;

  React.useEffect(() => {
    if (deleteScope && !deleteScopeMatches) {
      setDeleteScope((current) => (current === deleteScope ? null : current));
    }
  }, [deleteScope, deleteScopeMatches]);

  React.useEffect(() => {
    if (
      selectedId &&
      page === 'scripts' &&
      !rows.some((script) => script.id === selectedId) &&
      retainedDelete?.id !== selectedId
    )
      setSelectedId(null);
  }, [selectedId, rows, page, retainedDelete]);

  React.useEffect(() => {
    if (
      query.collectionId &&
      snapshot &&
      !snapshot.collections.some((collection) => collection.id === query.collectionId)
    ) {
      setQuery((previous) => nextQuery(previous, { collectionId: null }));
    }
  }, [snapshot, query.collectionId]);

  // Query/selection changes invalidate undispatched plans. Own pending snapshot revisions do not self-cancel.
  React.useEffect(() => {
    window.ahkManager.updateSelection(
      selectionKey && snapshot && ready
        ? { scriptId: selectionKey, query, stateRevision: snapshot.revision }
        : null,
    );
  }, [selectionKey, query, ready]);

  React.useEffect(() => setContext(null), [query]);

  React.useEffect(() => {
    if (!context) return;
    contextRef.current?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
    const close = (event: PointerEvent): void => {
      if (!contextRef.current?.contains(event.target as Node)) setContext(null);
    };
    window.addEventListener('pointerdown', close);
    return () => window.removeEventListener('pointerdown', close);
  }, [context]);

  const changeQuery = (patch: Partial<Omit<ScriptQuery, 'version'>>): void => {
    setQuery((previous) => nextQuery(previous, patch));
  };

  const select = (id: string): void => {
    setSelectedId(id);
    if (narrow) setShowDetails(true);
  };

  const navigateScript = (id: string): void => {
    setPage('scripts');
    changeQuery({ search: '', status: 'All', collectionId: null });
    select(id);
  };

  const showMetadata = (): void => {
    setShowDetails(true);
    setContext(null);
    requestAnimationFrame(() => {
      const expander = inspector.current?.querySelector<HTMLDetailsElement>('.metadata-expander');
      if (expander) {
        expander.open = true;
        expander.querySelector<HTMLInputElement>('input')?.focus();
      }
    });
  };

  return {
    page,
    setPage,
    query,
    changeQuery,
    selected,
    rows,
    setSelectedId,
    setDeleteScope,
    showDetails,
    setShowDetails,
    context,
    setContext,
    search,
    inspector,
    contextRef,
    select,
    navigateScript,
    showMetadata,
  };
}

export type LibrarySelection = ReturnType<typeof useLibrarySelection>;
