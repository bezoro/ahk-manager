import * as React from 'react';
import { DocumentRegular, CheckmarkRegular, SubtractRegular } from '@fluentui/react-icons';
import type { ScriptSnapshot } from '../../shared/contracts.js';
import { fileName, rowWindow } from '../model.js';
import { status, text } from '../resources/pt-BR.js';

interface Props { rows: ScriptSnapshot[]; selectedId: string | null; onSelect(id: string): void; onContext(id: string, x: number, y: number): void; queryVersion: string }
export function ScriptList({ rows, selectedId, onSelect, onContext, queryVersion }: Props): React.ReactElement {
  const viewport = React.useRef<HTMLDivElement>(null); const [scroll, setScroll] = React.useState(0); const [height, setHeight] = React.useState(480);
  React.useEffect(() => { const node = viewport.current; if (!node) return; const observer = new ResizeObserver(() => setHeight(node.clientHeight)); observer.observe(node); setHeight(node.clientHeight); return () => observer.disconnect(); }, []);
  React.useEffect(() => { if (viewport.current) viewport.current.scrollTop = 0; setScroll(0); }, [queryVersion]);
  const range = rowWindow(rows.length, scroll, height); const rendered = rows.slice(range.start, range.end);
  function navigate(event: React.KeyboardEvent<HTMLDivElement>): void {
    const index = rows.findIndex(row => row.id === selectedId); let next = index;
    if (event.key === 'ArrowDown') next = Math.min(rows.length - 1, index + 1); else if (event.key === 'ArrowUp') next = Math.max(0, index - 1); else if (event.key === 'Home') next = 0; else if (event.key === 'End') next = rows.length - 1;
    else if ((event.key === 'ContextMenu' || event.shiftKey && event.key === 'F10') && selectedId) { const bounds = viewport.current?.getBoundingClientRect(); if (bounds) onContext(selectedId, bounds.x + 30, bounds.y + 50); event.preventDefault(); return; } else return;
    const row = rows[next]; if (!row) return; event.preventDefault(); onSelect(row.id);
    const node = viewport.current; if (node) { const top = 38 + next * 68; if (top < node.scrollTop + 38) node.scrollTop = top - 38; else if (top + 68 > node.scrollTop + node.clientHeight) node.scrollTop = top + 68 - node.clientHeight; node.focus(); }
  }
  return <div className="script-viewport" ref={viewport} tabIndex={0} role="listbox" aria-label={text.scripts} aria-activedescendant={selectedId && rendered.some(row => row.id === selectedId) ? 'script-' + selectedId : undefined} onKeyDown={navigate} onScroll={event => setScroll(event.currentTarget.scrollTop)}>
    <div className="table-head" aria-hidden="true"><span>{text.name}</span><span>{text.status}</span><span>{text.mainHotkey}</span><span>{text.startupColumn}</span></div>
    <div style={{ height: range.start * 68 }} aria-hidden="true" />
    {rendered.map((script, index) => <button key={script.id} id={'script-' + script.id} role="option" tabIndex={-1} aria-selected={selectedId === script.id} aria-posinset={range.start + index + 1} aria-setsize={rows.length} aria-label={`${script.friendlyName}. ${status(script.status)}. ${text.startupColumn}: ${script.runAtLogin === null ? text.undefined : script.runAtLogin ? text.startupYes : text.startupNo}`} className={'script-row ' + (selectedId === script.id ? 'selected' : '')} onClick={() => onSelect(script.id)} onContextMenu={event => { event.preventDefault(); onContext(script.id, event.clientX, event.clientY); }}>
      <span className="name-cell"><DocumentRegular /><span><strong title={script.friendlyName}>{script.friendlyName}</strong><small title={script.path}>{fileName(script.path)}</small></span></span>
      <span className={'status-text status-' + script.status.toLowerCase()}><i />{status(script.status)}</span>
      <span className="row-hotkey">{script.hotkeys[0] ? <kbd title={script.hotkeys.map(key => key.chord).join(', ')}>{script.hotkeys[0].chord}</kbd> : <span className="muted">—</span>}</span>
      <span className={'startup-value ' + (script.runAtLogin === true ? 'startup-on' : '')}>{script.runAtLogin === true ? <CheckmarkRegular /> : script.runAtLogin === null ? <SubtractRegular /> : null}{script.runAtLogin === null ? text.undefined : script.runAtLogin ? text.startupYes : text.startupNo}</span>
    </button>)}
    <div style={{ height: Math.max(0, rows.length - range.end) * 68 }} aria-hidden="true" />
  </div>;
}
