import * as React from 'react';
import { Badge, Button, Input, Tooltip } from '@fluentui/react-components';
import { SearchRegular } from '@fluentui/react-icons';
import type { ManagerSnapshot, HotkeySnapshot } from '../../shared/contracts.js';
import { formatHotkey } from '../hotkeys.js';
import { text } from '../resources/en-US.js';

export function ReadableHotkey({ hotkey, compact = false }: { hotkey:HotkeySnapshot; compact?:boolean }):React.ReactElement {
  const value=formatHotkey(hotkey);
  return <span className={compact?'readable-hotkey compact':'readable-hotkey'}><Tooltip relationship="description" content={[value.instruction,...value.details].join('\n')}><kbd tabIndex={0} aria-label={value.instruction}>{value.label}</kbd></Tooltip>{!compact&&<details className="hotkey-explanation"><summary>{text.technicalDetails}</summary><p>{value.instruction}</p>{value.details.map((detail,index)=><p className="selectable" key={index}>{detail}</p>)}</details>}</span>;
}

export function Hotkeys({ snapshot, onScript }: { snapshot: ManagerSnapshot; onScript(id: string): void }): React.ReactElement {
  const [search, setSearch] = React.useState(''); const needle = search.trim().toLocaleLowerCase('en-US');
  const rows = snapshot.scripts.flatMap(script => script.hotkeys.map((key, index) => ({ script, key, id: script.id + ':' + index }))).filter(({ script, key }) => !needle || [script.friendlyName, script.path, key.chord, key.context, key.sourcePath,formatHotkey(key).label,formatHotkey(key).instruction].join(' ').toLocaleLowerCase('en-US').includes(needle));
  return <div className="data-page"><Input className="page-search" contentBefore={<SearchRegular />} aria-label={text.searchOther} placeholder={text.searchOther} value={search} onChange={(_, data) => setSearch(data.value)} /><div className="analysis-notice"><Badge appearance="tint">{text.partialAnalysis}</Badge><p>{text.analysisHint}</p></div><div className="page-scroll">{rows.length ? <table className="data-table"><thead><tr><th>{text.chord}</th><th>{text.script}</th><th>{text.context}</th></tr></thead><tbody>{rows.map(({ script, key, id }) => <tr key={id}><td><ReadableHotkey hotkey={key} />{key.possibleConflict && <span className="conflict-label">{text.possibleConflict}</span>}</td><td><Button appearance="transparent" className="text-button" onClick={() => onScript(script.id)}>{script.friendlyName}</Button><small className="break-path">{key.sourcePath ?? script.path}{key.line !== null ? ':' + key.line : ''}</small></td><td>{key.context || '—'}<small>{key.origin === 'Manual' ? text.manual : key.origin === 'Parsed' ? text.parsed : text.unanalysed}</small></td></tr>)}</tbody></table> : <div className="empty-page"><h2>{text.noHotkeys}</h2><p>{text.analysisHint}</p></div>}</div></div>;
}
