import * as React from 'react';
import { Badge, Button, Input, ProgressBar, Select, Spinner } from '@fluentui/react-components';
import { ArrowClockwiseRegular, ArrowDownloadRegular, HistoryRegular } from '@fluentui/react-icons';
import {
  isEarlierUpdateVersion,
  type UpdateChannel,
  type UpdateRelease,
  type UpdateState,
} from '../../shared/updates.js';
import { text } from '../resources/pt-BR.js';

interface Props {
  state: UpdateState;
  onState(value: UpdateState): void;
  installBlockedReason: string | null;
  recovery: boolean;
}

function date(value: string | null): string {
  if (!value) return text.updateDateUnavailable;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? text.updateDateUnavailable
    : parsed.toLocaleDateString('pt-BR', { day: '2-digit', month: 'long', year: 'numeric' });
}

function checkedAt(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? text.updateDateUnavailable
    : parsed.toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
}

export function Updates({
  state,
  onState,
  installBlockedReason,
  recovery,
}: Props): React.ReactElement {
  const [token, setToken] = React.useState('');
  const [pending, setPending] = React.useState(false);
  const [operation, setOperation] = React.useState<
    'check' | 'install' | 'token' | 'channel' | null
  >(null);
  const [feedback, setFeedback] = React.useState<string | null>(null);
  const inFlight = React.useRef(false);
  const active = pending || ['checking', 'downloading', 'installing'].includes(state.phase);
  const available = state.availableVersion
    ? state.releases.find(
        (release) =>
          release.version === state.availableVersion && !release.installed && release.canInstall,
      )
    : undefined;
  const installHint = installBlockedReason ?? text.updateInstallHint;
  const returningToStable =
    available &&
    state.channel === 'stable' &&
    isEarlierUpdateVersion(available.version, state.currentVersion);
  const availablePreview = available?.version.includes('-test.') ?? false;
  const feedbackFailed =
    !!state.error ||
    [
      text.updateAccessFailed,
      text.updateInstallFailed,
      text.updateCheckFailed,
      text.updateChannelFailed,
    ].some((message) => message === feedback);
  async function request(
    action: 'check' | 'install' | 'token' | 'channel',
    release?: UpdateRelease,
    credential?: string | null,
    channel?: UpdateChannel,
  ): Promise<void> {
    if (
      inFlight.current ||
      active ||
      (action === 'install' && (!release?.canInstall || installBlockedReason))
    )
      return;
    inFlight.current = true;
    setPending(true);
    setOperation(action);
    setFeedback(null);
    if (action === 'token') setToken('');
    try {
      const result =
        action === 'check'
          ? await window.ahkManager.checkUpdates()
          : action === 'install'
            ? await window.ahkManager.installUpdate(release!.id)
            : action === 'channel'
              ? await window.ahkManager.setUpdateChannel(channel!)
              : await window.ahkManager.setUpdateToken(credential ?? null);
      onState(result);
      if (action === 'token' && !result.error)
        setFeedback(credential ? text.updateAccessSaved : text.updateAccessRemoved);
    } catch {
      setFeedback(
        action === 'token'
          ? text.updateAccessFailed
          : action === 'install'
            ? text.updateInstallFailed
            : action === 'channel'
              ? text.updateChannelFailed
              : text.updateCheckFailed,
      );
    } finally {
      inFlight.current = false;
      setPending(false);
      setOperation(null);
    }
  }
  const install = (release: UpdateRelease, label: string, primary = false): React.ReactElement => (
    <Button
      appearance={primary ? 'primary' : 'secondary'}
      icon={primary ? <ArrowDownloadRegular /> : <HistoryRegular />}
      disabled={active || !!installBlockedReason || !release.canInstall}
      title={installHint}
      onClick={() => void request('install', release)}
    >
      {label}
    </Button>
  );
  return (
    <div className="updates-page data-page">
      <div className="updates-summary" aria-labelledby="updates-current-title">
        <div>
          <p className="muted" id="updates-current-title">
            {text.updateCurrentVersion}
          </p>
          <h2>{state.currentVersion || text.updateLoadingVersion}</h2>
          <p className="updates-availability">
            {state.availableVersion
              ? `${returningToStable ? text.updateStableAvailable : availablePreview ? text.updateTestAvailable : text.updateAvailableVersion} ${state.availableVersion}`
              : state.error
                ? text.updateLatestUnknown
                : state.lastCheckedAt
                  ? text.updateCurrent
                  : text.updateNotChecked}
          </p>
          <p className="muted">
            {state.lastCheckedAt
              ? `${text.updateLastChecked} ${checkedAt(state.lastCheckedAt)}`
              : text.updateAutomaticHint}
          </p>
        </div>
        <div className="updates-summary-actions">
          {available &&
            install(
              available,
              returningToStable
                ? text.updateRollback
                : availablePreview
                  ? text.updateInstallTest
                  : text.updateInstall,
              true,
            )}
          <Button
            icon={<ArrowClockwiseRegular />}
            disabled={active}
            onClick={() => void request('check')}
          >
            {text.updateCheck}
          </Button>
        </div>
      </div>
      <section className="update-channel" aria-labelledby="update-channel-label">
        <div className="update-channel-field">
          <label id="update-channel-label" htmlFor="update-channel">
            {text.updateChannel}
          </label>
          <Select
            id="update-channel"
            value={state.channel}
            disabled={active}
            aria-describedby="update-channel-hint"
            onChange={(_, data) => {
              if (data.value === 'stable' || data.value === 'test')
                void request('channel', undefined, undefined, data.value);
            }}
          >
            <option value="stable">{text.updateChannelStable}</option>
            <option value="test">{text.updateChannelTest}</option>
          </Select>
        </div>
        <p className="muted" id="update-channel-hint">
          {state.channel === 'test' ? text.updateChannelTestHint : text.updateChannelStableHint}
        </p>
      </section>
      {recovery && (
        <p className="update-recovery-notice" role="status">
          {text.updateRecoveryHint}
        </p>
      )}
      {installBlockedReason && (
        <p className="update-recovery-notice" role="status">
          {installBlockedReason}
        </p>
      )}
      {!installBlockedReason && (
        <p className="muted update-install-hint">{text.updateInstallHint}</p>
      )}
      {active && (
        <div className="update-progress" role="status" aria-live="polite">
          <Spinner size="tiny" />
          <span>
            {state.phase === 'downloading'
              ? text.updateDownloading
              : state.phase === 'installing' || operation === 'install'
                ? text.updateInstalling
                : operation === 'token'
                  ? text.updateSavingAccess
                  : operation === 'channel'
                    ? text.updateSavingChannel
                    : text.updateChecking}
          </span>
          {state.phase === 'downloading' && (
            <ProgressBar
              aria-label={text.updateDownloadProgress}
              value={
                state.progress === null ? undefined : Math.max(0, Math.min(1, state.progress / 100))
              }
            />
          )}
        </div>
      )}
      {(state.error || feedback) && (
        <p
          className={'update-feedback ' + (feedbackFailed ? 'warning' : 'success')}
          role={feedbackFailed ? 'alert' : 'status'}
        >
          {feedback ?? state.error}
        </p>
      )}
      <div className="updates-scroll page-scroll">
        <section className="update-history" aria-labelledby="update-history-title">
          <h2 id="update-history-title">{text.updateHistory}</h2>
          <p className="muted">{text.updateHistoryHint}</p>
          {state.releases.length ? (
            <ol className="update-release-list">
              {state.releases.map((release) => (
                <li key={release.id} className="update-release">
                  <div className="update-release-heading">
                    <div>
                      <h3>{release.version}</h3>
                      <p className="muted">{date(release.publishedAt)}</p>
                    </div>
                    <div className="update-release-badges">
                      {release.version.includes('-test.') && (
                        <Badge appearance="outline" color="warning">
                          {text.updatePreview}
                        </Badge>
                      )}
                      {release.installed ? (
                        <Badge appearance="tint" color="success">
                          {text.updateInstalled}
                        </Badge>
                      ) : release.version === state.availableVersion ? (
                        <Badge appearance="tint" color="brand">
                          {returningToStable ? text.updateStable : text.updateNew}
                        </Badge>
                      ) : null}
                    </div>
                  </div>
                  <p className="update-release-notes selectable">
                    {release.notes || text.updateNoNotes}
                  </p>
                  <div className="update-release-actions">
                    {!release.installed &&
                      install(
                        release,
                        isEarlierUpdateVersion(release.version, state.currentVersion)
                          ? text.updateRollback
                          : text.updateInstallVersion,
                      )}
                    <span className="muted">
                      {release.cached
                        ? text.updateCached
                        : !release.canInstall && !release.installed
                          ? text.updateInstallerUnavailable
                          : ''}
                    </span>
                  </div>
                </li>
              ))}
            </ol>
          ) : (
            <p className="update-history-empty">{text.updateNoHistory}</p>
          )}
        </section>
        <details className="update-access">
          <summary>{text.updateAccess}</summary>
          <p className="muted">{text.updateAccessHint}</p>
          <p className="update-access-state">
            {state.accessConfigured ? text.updateAccessConfigured : text.updateAccessMissing}
          </p>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (token.trim()) void request('token', undefined, token.trim());
            }}
            autoComplete="off"
          >
            <label htmlFor="update-token">{text.updateToken}</label>
            <Input
              id="update-token"
              type="password"
              maxLength={512}
              autoComplete="new-password"
              value={token}
              disabled={active}
              onChange={(_, data) => setToken(data.value)}
            />
            <div className="update-access-actions">
              <Button type="submit" disabled={active || !token.trim()}>
                {text.updateSaveAccess}
              </Button>
              <Button
                type="button"
                appearance="subtle"
                disabled={active || !state.accessConfigured}
                onClick={() => void request('token', undefined, null)}
              >
                {text.updateRemoveAccess}
              </Button>
            </div>
          </form>
        </details>
      </div>
    </div>
  );
}
