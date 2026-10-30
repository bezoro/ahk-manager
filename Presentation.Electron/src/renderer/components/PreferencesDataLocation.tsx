import * as React from 'react';
import { Button, Input } from '@fluentui/react-components';
import type { StorageState } from '../../shared/storage.js';

export interface PreferencesDataLocationProps {
  state: StorageState;
  busy: boolean;
  onChoose(): Promise<void>;
  onOpen(): Promise<void>;
  onUseDefault(): Promise<void>;
  onRestart(): Promise<void>;
  onCancelMove(): Promise<void>;
}

/** A native folder picker owns the choice; the renderer never submits a filesystem path. */
export function PreferencesDataLocation({
  state,
  busy,
  onChoose,
  onOpen,
  onUseDefault,
  onRestart,
  onCancelMove,
}: PreferencesDataLocationProps): React.ReactElement {
  const [working, setWorking] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const disabled = busy || working;
  const usingDefault =
    state.currentDataDirectory.toLowerCase() === state.defaultDataDirectory.toLowerCase();
  async function run(action: () => Promise<void>): Promise<void> {
    if (disabled) return;
    setWorking(true);
    setError(null);
    try {
      await action();
    } catch (failure) {
      setError(
        failure instanceof Error ? failure.message : 'The data location could not be changed.',
      );
    } finally {
      setWorking(false);
    }
  }
  return (
    <section className="preference-section" aria-labelledby="settings-data-location-title">
      <h3 id="settings-data-location-title">Data location</h3>
      <div className="picker-row">
        <Input aria-label="Current data folder" value={state.currentDataDirectory} readOnly />
        <Button disabled={disabled} onClick={() => void run(onOpen)}>
          Open folder
        </Button>
      </div>
      <p className="muted">Your settings, activity and downloaded versions are saved here.</p>
      {state.mode === 'portable' ? (
        <>
          <div className="button-row">
            <Button disabled={disabled || !state.canChange} onClick={() => void run(onChoose)}>
              Change location…
            </Button>
            <Button
              appearance="subtle"
              disabled={disabled || !state.canChange || usingDefault}
              onClick={() => void run(onUseDefault)}
            >
              Use default
            </Button>
          </div>
          <p className="muted">
            The default is the Data folder beside the portable launcher. Choose an empty folder to
            transfer your data. Manager will restart; running scripts will keep running. The
            previous folder is kept as a backup.
          </p>
        </>
      ) : (
        <p className="muted">The portable edition lets you choose a different data folder.</p>
      )}
      {state.pendingDataDirectory && (
        <div role="status">
          <p>Prepared data location: {state.pendingDataDirectory}</p>
          <div className="button-row">
            <Button appearance="primary" disabled={disabled} onClick={() => void run(onRestart)}>
              Restart Manager
            </Button>
            <Button disabled={disabled} onClick={() => void run(onCancelMove)}>
              Cancel move
            </Button>
          </div>
        </div>
      )}
      {(error || state.error) && <p role="alert">{error || state.error}</p>}
    </section>
  );
}
