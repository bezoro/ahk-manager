import * as React from 'react';
import { Checkbox, Tooltip } from '@fluentui/react-components';
import { DocumentRegular } from '@fluentui/react-icons';
import type { ScriptSnapshot } from '../../shared/contracts.js';
import { fileName, rowWindow, type RowPreference } from '../model.js';
import { status, text } from '../resources/en-US.js';
import { ReadableHotkey } from './Hotkeys.js';

interface Props {
  rows: ScriptSnapshot[];
  selectedId: string | null;
  onSelect(id: string): void;
  onContext(id: string, x: number, y: number): void;
  queryVersion: string;
  preferencesDisabled: boolean;
  pendingPreference: { scriptId: string; field: RowPreference } | null;
  onPreference(id: string, field: RowPreference, value: boolean): void;
}
export function ScriptList({
  rows,
  selectedId,
  onSelect,
  onContext,
  queryVersion,
  preferencesDisabled,
  pendingPreference,
  onPreference,
}: Props): React.ReactElement {
  const viewport = React.useRef<HTMLDivElement>(null);
  const grid = React.useRef<HTMLTableElement>(null);
  const [scroll, setScroll] = React.useState(0);
  const [height, setHeight] = React.useState(480);
  React.useEffect(() => {
    const node = viewport.current;
    if (!node) return;
    const observer = new ResizeObserver(() => setHeight(node.clientHeight));
    observer.observe(node);
    setHeight(node.clientHeight);
    return () => observer.disconnect();
  }, []);
  React.useEffect(() => {
    if (viewport.current) viewport.current.scrollTop = 0;
    setScroll(0);
  }, [queryVersion]);
  const range = rowWindow(rows.length, scroll, height);
  const rendered = rows.slice(range.start, range.end);
  function navigate(event: React.KeyboardEvent<HTMLTableElement>): void {
    if (event.target instanceof HTMLElement && event.target.closest('input,[role="checkbox"]'))
      return;
    const index = rows.findIndex((row) => row.id === selectedId);
    let next = index;
    if (event.key === 'ArrowDown') next = Math.min(rows.length - 1, index + 1);
    else if (event.key === 'ArrowUp') next = Math.max(0, index - 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = rows.length - 1;
    else if (
      (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) &&
      selectedId
    ) {
      const bounds = viewport.current?.getBoundingClientRect();
      if (bounds) onContext(selectedId, bounds.x + 30, bounds.y + 50);
      event.preventDefault();
      return;
    } else return;
    const row = rows[next];
    if (!row) return;
    event.preventDefault();
    onSelect(row.id);
    const node = viewport.current;
    if (node) {
      const top = 38 + next * 68;
      if (top < node.scrollTop + 38) node.scrollTop = top - 38;
      else if (top + 68 > node.scrollTop + node.clientHeight)
        node.scrollTop = top + 68 - node.clientHeight;
      grid.current?.focus();
    }
  }
  const preference = (script: ScriptSnapshot, field: RowPreference): React.ReactElement => {
    const startup = field === 'runAtLogin';
    const label =
      (startup ? text.startupColumn : text.adminPreference) +
      ': ' +
      script.friendlyName +
      (startup && script.runAtLogin === null ? ' · ' + text.undefined : '');
    const hint =
      script.runAtLogin === true && script.runAsAdministrator
        ? text.adminLoginHint
        : startup && script.runAtLogin === null
          ? text.startupUndefinedHint
          : startup
            ? text.runAtLogin
            : text.adminPreferenceHint;
    return (
      <td
        className="preference-cell"
        aria-busy={pendingPreference?.scriptId === script.id && pendingPreference.field === field}
      >
        <Tooltip relationship="description" content={hint}>
          <Checkbox
            aria-label={label}
            checked={script[field] === true}
            disabled={preferencesDisabled}
            onChange={(_, data) => onPreference(script.id, field, data.checked === true)}
          />
        </Tooltip>
      </td>
    );
  };
  return (
    <div
      className="script-viewport"
      ref={viewport}
      onScroll={(event) => setScroll(event.currentTarget.scrollTop)}
    >
      <table
        className="script-table"
        ref={grid}
        tabIndex={0}
        role="grid"
        aria-label={text.scripts}
        aria-rowcount={rows.length + 1}
        aria-colcount={5}
        aria-activedescendant={
          selectedId && rendered.some((row) => row.id === selectedId)
            ? 'script-' + selectedId
            : undefined
        }
        onKeyDown={navigate}
      >
        <colgroup>
          <col style={{ width: '34%' }} />
          <col style={{ width: '14%' }} />
          <col style={{ width: '22%' }} />
          <col style={{ width: '15%' }} />
          <col style={{ width: '15%' }} />
        </colgroup>
        <thead>
          <tr className="table-head">
            <th scope="col">{text.name}</th>
            <th scope="col">{text.status}</th>
            <th scope="col">{text.mainHotkey}</th>
            <th scope="col">{text.startupColumn}</th>
            <th scope="col">{text.administratorColumn}</th>
          </tr>
        </thead>
        <tbody>
          <tr className="virtual-spacer" aria-hidden="true">
            <td colSpan={5} style={{ height: range.start * 68 }} />
          </tr>
          {rendered.map((script, index) => (
            <tr
              key={script.id}
              id={'script-' + script.id}
              aria-selected={selectedId === script.id}
              aria-rowindex={range.start + index + 2}
              className={'script-row ' + (selectedId === script.id ? 'selected' : '')}
              onContextMenu={(event) => {
                event.preventDefault();
                onContext(script.id, event.clientX, event.clientY);
              }}
            >
              <td>
                <button
                  className="row-select name-cell"
                  aria-pressed={selectedId === script.id}
                  onClick={() => onSelect(script.id)}
                >
                  <DocumentRegular />
                  <span>
                    <strong title={script.friendlyName}>{script.friendlyName}</strong>
                    <small title={script.path}>{fileName(script.path)}</small>
                  </span>
                </button>
              </td>
              <td>
                <span className={'status-text status-' + script.status.toLowerCase()}>
                  <i />
                  {status(script.status)}
                </span>
              </td>
              <td className="row-hotkey">
                {script.hotkeys[0] ? (
                  <ReadableHotkey hotkey={script.hotkeys[0]} compact />
                ) : (
                  <span className="muted">—</span>
                )}
              </td>
              {preference(script, 'runAtLogin')}
              {preference(script, 'runAsAdministrator')}
            </tr>
          ))}
          <tr className="virtual-spacer" aria-hidden="true">
            <td colSpan={5} style={{ height: Math.max(0, rows.length - range.end) * 68 }} />
          </tr>
        </tbody>
      </table>
    </div>
  );
}
