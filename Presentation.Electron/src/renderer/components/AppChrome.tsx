import * as React from 'react';
import {
  Button,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Tooltip,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowClockwiseRegular,
  ChevronDownRegular,
  DocumentRegular,
  PanelRightRegular,
} from '@fluentui/react-icons';
import type { HostState, ManagerSnapshot } from '../../shared/contracts.js';
import type { Page } from '../appTypes.js';
import { connectionStatus, problemCount, scriptCount, text } from '../resources/en-US.js';
import { time } from '../format.js';

interface NativeTitleProps {
  host: HostState;
  busy: boolean;
  onRequestAdministration(): void;
}
export function NativeTitle({
  host,
  busy,
  onRequestAdministration,
}: NativeTitleProps): React.ReactElement {
  const ready = host.connection === 'ready';
  return (
    <header className="native-title">
      <DocumentRegular />
      <span>{text.app}</span>
      <span className="authority-badge" role="status">
        {host.authorityMode === 'Administrator'
          ? text.authorityAdministrator
          : host.authorityMode === 'Normal'
            ? text.authorityNormal
            : text.authorityUnverified}
      </span>
      {ready && host.authorityMode === 'Normal' && (
        <Button
          className="administration-action"
          size="small"
          disabled={!host.administrationPending && busy}
          onClick={onRequestAdministration}
        >
          {host.administrationPending ? text.checkAdministration : text.requestAdministration}
        </Button>
      )}
    </header>
  );
}

interface PageHeadingProps {
  page: Page;
  snapshot: ManagerSnapshot | null;
  ready: boolean;
  busy: boolean;
  readonly: boolean;
  narrow: boolean;
  showDetails: boolean;
  onRefresh(): void;
  onAdd(folder?: boolean): void;
  onToggleDetails(): void;
}
export function PageHeading({
  page,
  snapshot,
  ready,
  busy,
  readonly,
  narrow,
  showDetails,
  onRefresh,
  onAdd,
  onToggleDetails,
}: PageHeadingProps): React.ReactElement {
  const pageTitle =
    page === 'scripts'
      ? text.scripts
      : page === 'hotkeys'
        ? text.hotkeys
        : page === 'updates'
          ? text.updates
          : text.activity;
  return (
    <div className="page-heading">
      <div>
        <h1>{pageTitle}</h1>
        <p>
          {page === 'updates'
            ? text.updatesDescription
            : snapshot
              ? page === 'scripts'
                ? scriptCount(
                    snapshot.scripts.length,
                    snapshot.scripts.filter((script) => script.status === 'Running').length,
                  )
                : page === 'hotkeys'
                  ? text.hotkeysDescription
                  : text.activityDescription
              : text.starting}
        </p>
      </div>
      <div className="heading-actions">
        {page !== 'updates' && (
          <Tooltip content={text.refresh} relationship="label">
            <Button
              icon={<ArrowClockwiseRegular />}
              aria-label={text.refresh}
              disabled={!ready || busy}
              onClick={onRefresh}
            />
          </Tooltip>
        )}
        {page === 'scripts' && (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button appearance="primary" icon={<AddRegular />} disabled={readonly || busy}>
                {text.add}
                <ChevronDownRegular />
              </Button>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem onClick={() => onAdd()}>{text.addFiles}</MenuItem>
                <MenuItem onClick={() => onAdd(true)}>{text.addFolder}</MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        {page === 'scripts' && narrow && (
          <Button
            icon={<PanelRightRegular />}
            aria-label={text.details}
            aria-expanded={showDetails}
            onClick={onToggleDetails}
          />
        )}
      </div>
    </div>
  );
}

export function StatusFooter({
  host,
  busy,
}: {
  host: HostState;
  busy: boolean;
}): React.ReactElement {
  const snapshot = host.snapshot;
  return (
    <footer
      className="status-footer"
      aria-label={text.statusSummary}
      role="status"
      aria-live="polite"
      aria-atomic="true"
    >
      <span
        className="footer-state"
        title={connectionStatus(host.connection) + (busy ? ' · ' + text.pending : '')}
      >
        {connectionStatus(host.connection)}
        {busy ? ' · ' + text.pending : ''}
      </span>
      <span className="footer-counts">
        {snapshot
          ? scriptCount(
              snapshot.scripts.length,
              snapshot.scripts.filter((script) => script.status === 'Running').length,
            )
          : ''}
        {snapshot?.problemCount !== undefined ? ' · ' + problemCount(snapshot.problemCount) : ''}
      </span>
      <span className="footer-observed">
        {snapshot?.observedAt
          ? text.observedAt + ' ' + time(snapshot.observedAt)
          : text.neverObserved}
      </span>
    </footer>
  );
}

export function UpdateBanner({
  version,
  onOpen,
}: {
  version: string;
  onOpen(): void;
}): React.ReactElement {
  return (
    <div className="update-available-banner" role="status">
      <span>
        {text.updateAvailableVersion} {version}
      </span>
      <Button size="small" onClick={onOpen}>
        {text.updateOpen}
      </Button>
    </div>
  );
}
