import * as React from 'react';
import type { ManagerSettings } from '../../shared/contracts.js';
import type { Dialogs } from '../hooks/useDialogs.js';
import { CollectionDialog, HelpDialog, ImportDialog } from './Dialogs.js';
import { Preferences } from './Preferences.js';

interface Props {
  dialogs: Dialogs;
  settings: ManagerSettings | null;
  busy: boolean;
  readonly: boolean;
  ready: boolean;
  onSaveSettings(value: ManagerSettings): void;
  onPick(kind: 'editor' | 'interpreter'): Promise<string | null>;
  onTransferStartup(): void;
  onImport(): void;
  onBackup(): void;
  onSaveCollection(id: string | null, name: string): void;
  onCommitImport(token: string): void;
}

export function AppDialogs({
  dialogs,
  settings,
  busy,
  readonly,
  ready,
  onSaveSettings,
  onPick,
  onTransferStartup,
  onImport,
  onBackup,
  onSaveCollection,
  onCommitImport,
}: Props): React.ReactElement {
  const { preferences, help, collection, preview, notice, close, setNotice } = dialogs;
  return (
    <>
      {preferences && settings && (
        <Preferences
          settings={settings}
          busy={busy}
          readonly={readonly}
          notice={notice}
          onDismissNotice={() => setNotice(null)}
          onClose={close}
          onSave={onSaveSettings}
          onPick={onPick}
          onTransferStartup={onTransferStartup}
        />
      )}
      {help && (
        <HelpDialog
          busy={busy || !ready}
          onOpenLogs={() => window.ahkManager.openLogs()}
          onCopyDiagnostics={(mode) => window.ahkManager.copyDiagnostics(mode)}
          onClose={() => dialogs.setHelp(false)}
          onImport={onImport}
          onBackup={() => {
            dialogs.setHelp(false);
            onBackup();
          }}
        />
      )}
      {collection && (
        <CollectionDialog
          initialName={collection.name}
          rename={collection.id !== null}
          busy={busy}
          notice={notice}
          onDismissNotice={() => setNotice(null)}
          onClose={close}
          onSave={(name) => onSaveCollection(collection.id, name)}
        />
      )}
      {preview && (
        <ImportDialog
          preview={preview.preview}
          busy={busy}
          notice={notice}
          onDismissNotice={() => setNotice(null)}
          onClose={close}
          onCommit={() => onCommitImport(preview.token)}
        />
      )}
    </>
  );
}
