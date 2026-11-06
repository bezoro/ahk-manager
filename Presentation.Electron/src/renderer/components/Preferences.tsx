import * as React from 'react';
import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  Textarea,
} from '@fluentui/react-components';
import type { ManagerSettings, OperationResult } from '../../shared/contracts.js';
import { text } from '../resources/en-US.js';
import type { Notice } from './OperationNotice.js';
import { CopyableOperationNotice } from './CopyableOperationNotice.js';
import { PreferencesDataLocation } from './PreferencesDataLocation.js';
import { PreferencesStartupRepair } from './PreferencesStartupRepair.js';
import { useDataLocation } from '../hooks/useDataLocation.js';

interface Props {
  settings: ManagerSettings;
  busy: boolean;
  readonly: boolean;
  notice: Notice | null;
  onDismissNotice(): void;
  onClose(): void;
  onSave(value: ManagerSettings): void;
  onPick(kind: 'interpreter' | 'editor'): Promise<string | null>;
  onTransferStartup(): void;
  onCopy(value: string): Promise<boolean>;
  onRepairResult(result: OperationResult): void;
  onRepairStartup(fingerprint: string): Promise<OperationResult>;
}

export function Preferences({
  settings,
  busy,
  readonly,
  notice,
  onDismissNotice,
  onClose,
  onSave,
  onPick,
  onTransferStartup,
  onCopy,
  onRepairResult,
  onRepairStartup,
}: Props): React.ReactElement {
  const [draft, setDraft] = React.useState(() => ({
    ...settings,
    launchManagerAsAdministrator: true,
    editorArguments: [...settings.editorArguments],
  }));
  const [argumentsText, setArgumentsText] = React.useState(settings.editorArguments.join('\n'));
  const [picking, setPicking] = React.useState(false);
  const [repairingStartup, setRepairingStartup] = React.useState(false);
  const storage = useDataLocation(onClose);
  const storageStatus =
    storage.error || storage.state?.error ? 'failed' : storage.state ? 'ready' : 'loading';
  const settingsChanged =
    JSON.stringify({
      ...draft,
      launchManagerAsAdministrator: true,
      window: settings.window,
      editorArguments: argumentsText.split(/\r?\n/).filter(Boolean),
    }) !== JSON.stringify({ ...settings, launchManagerAsAdministrator: true });
  const change = (patch: Partial<ManagerSettings>) => setDraft((value) => ({ ...value, ...patch }));
  async function choose(kind: 'interpreter' | 'editor'): Promise<void> {
    if (picking || busy) return;
    setPicking(true);
    try {
      const path = await onPick(kind);
      if (path) change(kind === 'editor' ? { editorPath: path } : { interpreterPath: path });
    } finally {
      setPicking(false);
    }
  }
  const save = (): void =>
    onSave({
      ...draft,
      launchManagerAsAdministrator: true,
      window: settings.window,
      editorArguments: argumentsText.split(/\r?\n/).filter((value) => value.length > 0),
    });
  const working = busy || repairingStartup;
  return (
    <Dialog
      open
      onOpenChange={(_, data) => {
        if (!data.open && !working) onClose();
      }}
    >
      <DialogSurface className="preferences-dialog">
        <DialogBody>
          <DialogTitle>{text.preferences}</DialogTitle>
          <DialogContent>
            <CopyableOperationNotice notice={notice} onDismiss={onDismissNotice} onCopy={onCopy} />
            <fieldset disabled={readonly || working || picking || storage.busy}>
              <legend className="sr-only">{text.preferences}</legend>
              <section
                className="preference-section startup-preferences"
                aria-labelledby="settings-startup-title"
              >
                <h3 id="settings-startup-title">{text.startup}</h3>
                <Checkbox
                  id="settings-launch-at-login"
                  checked={draft.launchManagerAtLogin}
                  label={text.launchManager}
                  onChange={(_, data) => change({ launchManagerAtLogin: data.checked === true })}
                />
                <p className="muted">{text.startupHint}</p>
                <p id="settings-administrator-policy">
                  <strong>{text.managerAdministratorRequired}</strong>
                </p>
                <p className="muted">{text.managerAdministratorHint}</p>
                <Checkbox
                  id="settings-start-in-tray"
                  checked={draft.startInTray}
                  label={text.startInTray}
                  onChange={(_, data) => change({ startInTray: data.checked === true })}
                />
                <p className="muted">{text.startInTrayHint}</p>
                <Checkbox
                  id="settings-show-at-login"
                  checked={draft.showWindowAtLogin}
                  disabled={draft.startInTray}
                  label={text.showAtLogin}
                  onChange={(_, data) => change({ showWindowAtLogin: data.checked === true })}
                />
                <PreferencesStartupRepair
                  enabled={!readonly}
                  disabled={
                    readonly ||
                    busy ||
                    picking ||
                    storage.busy ||
                    storageStatus !== 'ready' ||
                    !!storage.state?.pendingDataDirectory ||
                    settingsChanged
                  }
                  settingsChanged={settingsChanged}
                  pendingDataMove={!!storage.state?.pendingDataDirectory}
                  storageStatus={storageStatus}
                  refreshKey={notice}
                  savedMode={JSON.stringify([settings.launchManagerAtLogin, true])}
                  onPending={setRepairingStartup}
                  onResult={onRepairResult}
                  onRepair={onRepairStartup}
                  onCopy={onCopy}
                />
              </section>
              {storage.state ? (
                <>
                  <PreferencesDataLocation
                    state={storage.state}
                    busy={working || storage.busy || picking || settingsChanged}
                    onChoose={storage.choose}
                    onOpen={storage.open}
                    onUseDefault={storage.useDefault}
                    onRestart={storage.restart}
                    onCancelMove={storage.cancelMove}
                  />
                  {settingsChanged && (
                    <p className="muted">
                      Save your preferences before changing the data location.
                    </p>
                  )}
                </>
              ) : storage.error ? (
                <p role="alert">{storage.error}</p>
              ) : null}
              <section className="preference-section" aria-labelledby="settings-editor-title">
                <h3 id="settings-editor-title">{text.editor}</h3>
                <div className="picker-row">
                  <Input
                    aria-label={text.editor}
                    readOnly
                    value={draft.editorPath ?? ''}
                    placeholder={text.editorDefault}
                  />
                  <Button onClick={() => void choose('editor')}>{text.choose}</Button>
                  <Button appearance="subtle" onClick={() => change({ editorPath: null })}>
                    {text.clear}
                  </Button>
                </div>
                <p className="muted">{text.safeEditHint}</p>
                <details className="preference-advanced">
                  <summary>{text.editorAdvanced}</summary>
                  <label htmlFor="settings-editor-arguments">{text.editorArguments}</label>
                  <Textarea
                    id="settings-editor-arguments"
                    resize="vertical"
                    value={argumentsText}
                    onChange={(_, data) => setArgumentsText(data.value)}
                    rows={3}
                  />
                  <p className="muted">{text.editorArgumentsHint}</p>
                </details>
              </section>
              <section className="preference-section" aria-labelledby="settings-appearance-title">
                <h3 id="settings-appearance-title">{text.appearance}</h3>
                <label htmlFor="settings-theme">{text.theme}</label>
                <select
                  id="settings-theme"
                  value={draft.theme}
                  onChange={(event) =>
                    change({ theme: event.target.value as ManagerSettings['theme'] })
                  }
                >
                  <option value="System">{text.system}</option>
                  <option value="Light">{text.light}</option>
                  <option value="Dark">{text.dark}</option>
                </select>
                <p className="muted">{text.themeHint}</p>
                <label htmlFor="settings-close-behavior">{text.closeBehavior}</label>
                <select
                  id="settings-close-behavior"
                  value={draft.closeBehavior}
                  onChange={(event) =>
                    change({
                      closeBehavior: event.target.value as ManagerSettings['closeBehavior'],
                    })
                  }
                >
                  <option value="HideToTray">{text.hideToTray}</option>
                  <option value="Exit">{text.exit}</option>
                </select>
                <p className="muted">{text.exitHint}</p>
              </section>
              <section className="preference-section" aria-labelledby="settings-interpreter-title">
                <h3 id="settings-interpreter-title">{text.interpreter}</h3>
                <div className="picker-row">
                  <Input
                    aria-label={text.interpreter}
                    readOnly
                    value={draft.interpreterPath ?? ''}
                    placeholder={text.notConfigured}
                  />
                  <Button onClick={() => void choose('interpreter')}>{text.choose}</Button>
                  <Button appearance="subtle" onClick={() => change({ interpreterPath: null })}>
                    {text.clear}
                  </Button>
                </div>
                <details className="preference-advanced">
                  <summary>{text.startupAdvanced}</summary>
                  <Button onClick={onTransferStartup}>{text.transferStartup}</Button>
                  <p className="muted">{text.transferStartupHint}</p>
                </details>
              </section>
            </fieldset>
          </DialogContent>
          <DialogActions>
            <Button disabled={working} onClick={onClose}>
              {text.cancel}
            </Button>
            <Button appearance="primary" disabled={readonly || working || picking} onClick={save}>
              {text.save}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
