import * as React from 'react';
import { Button, Input } from '@fluentui/react-components';
import type { StorageState } from '../../shared/storage.js';
import { text } from '../resources/en-US.js';

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
      setError(failure instanceof Error ? failure.message : text.dataLocationChangeFailed);
    } finally {
      setWorking(false);
    }
  }
  return (
    <section className="preference-section" aria-labelledby="settings-data-location-title">
      <h3 id="settings-data-location-title">{text.dataLocation}</h3>
      <div className="picker-row">
        <Input aria-label={text.currentDataFolder} value={state.currentDataDirectory} readOnly />
        <Button disabled={disabled} onClick={() => void run(onOpen)}>
          {text.openFolder}
        </Button>
      </div>
      <p className="muted">{text.dataLocationSavedHint}</p>
      <div className="button-row">
        <Button disabled={disabled || !state.canChange} onClick={() => void run(onChoose)}>
          {text.changeDataLocation}
        </Button>
        <Button
          appearance="subtle"
          disabled={disabled || !state.canChange || usingDefault}
          onClick={() => void run(onUseDefault)}
        >
          {text.useDefaultDataLocation}
        </Button>
      </div>
      <p className="muted">{text.dataLocationHint}</p>
      {state.pendingDataDirectory && (
        <div role="status">
          <p>
            {text.preparedDataLocation} {state.pendingDataDirectory}
          </p>
          <div className="button-row">
            <Button appearance="primary" disabled={disabled} onClick={() => void run(onRestart)}>
              {text.restartManager}
            </Button>
            <Button disabled={disabled} onClick={() => void run(onCancelMove)}>
              {text.cancelDataMove}
            </Button>
          </div>
        </div>
      )}
      {(error || state.error) && <p role="alert">{error || state.error}</p>}
    </section>
  );
}
