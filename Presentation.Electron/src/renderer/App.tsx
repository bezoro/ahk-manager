import * as React from 'react';
import { createDarkTheme, createLightTheme, FluentProvider } from '@fluentui/react-components';
import type { Page } from './appTypes.js';
import { brandPalette, newDraft } from './model.js';
import { useHost, useNarrow, useTheme, useUpdates } from './hooks.js';
import { useAdministration } from './hooks/useAdministration.js';
import { useDialogs } from './hooks/useDialogs.js';
import { useKeyboardShortcuts } from './hooks/useKeyboardShortcuts.js';
import { useLibrarySelection } from './hooks/useLibrarySelection.js';
import { useManagerActions } from './hooks/useManagerActions.js';
import { useMetadataDrafts } from './hooks/useMetadataDrafts.js';
import { useOperations } from './hooks/useOperations.js';
import { Activity } from './components/Activity.js';
import { AppDialogs } from './components/AppDialogs.js';
import { AppNavigation } from './components/AppNavigation.js';
import { NativeTitle, PageHeading, StatusFooter, UpdateBanner } from './components/AppChrome.js';
import { DisconnectedPage } from './components/DisconnectedPage.js';
import { Hotkeys } from './components/Hotkeys.js';
import { InspectorPanel } from './components/InspectorPanel.js';
import { LibraryNotices } from './components/LibraryNotices.js';
import { OperationNotice } from './components/OperationNotice.js';
import { ScriptContextMenu } from './components/ScriptContextMenu.js';
import { ScriptsPage } from './components/ScriptsPage.js';
import { Updates } from './components/Updates.js';

export function App(): React.ReactElement {
  const host = useHost();
  const updates = useUpdates();
  const appearance = useTheme();
  const narrow = useNarrow();
  const dialogs = useDialogs();
  const operations = useOperations(host, dialogs);
  const library = useLibrarySelection(host, narrow);
  const metadata = useMetadataDrafts(host.snapshot, operations);
  const administration = useAdministration(host, dialogs, metadata, operations);
  const actions = useManagerActions(host, library, metadata, dialogs, operations);
  useKeyboardShortcuts(host, library, dialogs, operations, actions);

  const { snapshot } = host;
  const { page, query, selected, showDetails } = library;
  const busy = operations.pending || host.busy || host.administrationPending;
  const ready = host.connection === 'ready';
  const readonly = !ready || !!snapshot?.readOnly;
  const theme = React.useMemo(() => {
    const value = (appearance.dark ? createDarkTheme : createLightTheme)(
      brandPalette(appearance.accent),
    );
    return { ...value, fontFamilyBase: '"Segoe UI Variable", "Segoe UI", sans-serif' };
  }, [appearance.dark, appearance.accent]);

  const navigate = (destination: Page): void => {
    library.setPage(destination);
    library.setContext(null);
    if (destination === 'updates') operations.setNotice(null);
    if (destination === 'scripts') library.changeQuery({ collectionId: null });
  };
  const openUpdates = (): void => {
    operations.setNotice(null);
    library.setPage('updates');
  };
  const retryBackend = (): void => {
    void operations.run(() => window.ahkManager.retryBackend());
  };

  return (
    <FluentProvider
      applyStylesToPortals={false}
      theme={theme}
      className="app-root"
      data-material={appearance.material}
      data-dark={appearance.dark}
      data-contrast={appearance.highContrast}
      data-reduced-motion={appearance.reducedMotion}
    >
      <NativeTitle host={host} busy={busy} onRequestAdministration={administration.request} />
      <div className="app-shell" inert={host.administrationPending}>
        <AppNavigation
          page={page}
          query={query}
          collections={snapshot?.collections}
          availableVersion={updates.state.availableVersion}
          readonly={readonly}
          busy={busy}
          onNavigate={navigate}
          onCollection={(id) => {
            library.setPage('scripts');
            library.changeQuery({ collectionId: id });
          }}
          onOpenCollection={dialogs.openCollection}
          onRemoveCollection={(id) => {
            if (snapshot)
              operations.perform(() => window.ahkManager.removeCollection(id, snapshot.revision));
          }}
          onPreferences={dialogs.openPreferences}
          onHelp={() => dialogs.setHelp(true)}
        />
        <main
          className={
            'workspace ' +
            (page === 'scripts' ? 'with-inspector' : '') +
            (showDetails ? ' details-open' : '')
          }
        >
          <div className="main-column">
            <PageHeading
              page={page}
              snapshot={snapshot}
              ready={ready}
              busy={busy}
              readonly={readonly}
              narrow={narrow}
              showDetails={showDetails}
              onRefresh={() => void operations.run(() => window.ahkManager.refresh())}
              onAdd={actions.add}
              onToggleDetails={() => library.setShowDetails((value) => !value)}
            />
            {page !== 'updates' && updates.state.availableVersion && (
              <UpdateBanner version={updates.state.availableVersion} onOpen={openUpdates} />
            )}
            <OperationNotice
              notice={operations.notice}
              onDismiss={() => operations.setNotice(null)}
              onCopy={operations.copy}
            />
            {page !== 'updates' && (
              <LibraryNotices
                host={host}
                busy={busy}
                onRetry={retryBackend}
                onRecover={() => operations.perform(() => window.ahkManager.recoverPending())}
                onRestoreBackup={() => operations.perform(() => window.ahkManager.restoreBackup())}
                onImport={actions.openImport}
              />
            )}
            {page === 'updates' ? (
              <Updates
                state={updates.state}
                onState={updates.accept}
                installBlockedReason={administration.updateBlockedReason}
                recovery={host.connection === 'failed'}
              />
            ) : !snapshot ? (
              <DisconnectedPage host={host} busy={busy} onRetry={retryBackend} />
            ) : page === 'scripts' ? (
              <ScriptsPage
                rows={library.rows}
                selectedId={selected?.id ?? null}
                query={query}
                scriptCount={snapshot.scripts.length}
                readonly={readonly}
                busy={busy}
                searchRef={library.search}
                pendingPreference={metadata.pendingPreference}
                onQuery={library.changeQuery}
                onSelect={library.select}
                onContext={library.setContext}
                onPreference={metadata.savePreference}
                onAdd={() => actions.add()}
              />
            ) : page === 'hotkeys' ? (
              <Hotkeys snapshot={snapshot} onScript={library.navigateScript} />
            ) : (
              <Activity snapshot={snapshot} onCopy={operations.copy} onError={operations.onError} />
            )}
          </div>
          {page === 'scripts' && (
            <InspectorPanel
              inspectorRef={library.inspector}
              narrow={narrow}
              showDetails={showDetails}
              onHideDetails={() => library.setShowDetails(false)}
              host={host}
              script={selected}
              draft={selected ? (metadata.drafts[selected.id] ?? newDraft(selected)) : null}
              busy={busy}
              onDraft={(draft) => {
                if (!host.administrationPending && selected)
                  metadata.setDrafts((previous) => ({ ...previous, [selected.id]: draft }));
              }}
              onSave={() => {
                if (selected) metadata.saveDraft(selected.id);
              }}
              onDiscard={() => {
                if (selected)
                  metadata.setDrafts((previous) => ({
                    ...previous,
                    [selected.id]: newDraft(selected),
                  }));
              }}
              onAction={actions.action}
              onCopy={operations.copy}
              onLocate={() => actions.fileAction('locate')}
              onRemove={actions.remove}
              onDelete={() => actions.fileAction('delete')}
            />
          )}
        </main>
      </div>
      <StatusFooter host={host} busy={busy} />
      {library.context && selected?.id === library.context.id && !dialogs.open && (
        <ScriptContextMenu
          host={host}
          selected={selected}
          context={library.context}
          contextRef={library.contextRef}
          busy={busy}
          readonly={readonly}
          draftDirty={!!metadata.drafts[selected.id]?.dirty}
          onClose={() => library.setContext(null)}
          onAction={actions.action}
          onMetadata={library.showMetadata}
          onRemove={actions.remove}
        />
      )}
      <AppDialogs
        dialogs={dialogs}
        settings={snapshot?.settings ?? null}
        busy={busy}
        readonly={readonly}
        ready={ready}
        onSaveSettings={administration.saveSettings}
        onPick={actions.pickExecutable}
        onTransferStartup={() =>
          operations.performDialog(() => window.ahkManager.transferStartup(), false)
        }
        onImport={actions.openImport}
        onBackup={() => operations.perform(() => window.ahkManager.restoreBackup())}
        onSaveCollection={(id, name) =>
          operations.performDialog(() => window.ahkManager.saveCollection(id, name))
        }
        onCommitImport={(token) =>
          operations.performDialog(() => window.ahkManager.commitImport(token))
        }
      />
    </FluentProvider>
  );
}
