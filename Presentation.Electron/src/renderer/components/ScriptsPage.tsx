import * as React from 'react';
import { Button, Input } from '@fluentui/react-components';
import { AddRegular, DocumentRegular, SearchRegular } from '@fluentui/react-icons';
import type { ScriptQuery, ScriptSnapshot } from '../../shared/contracts.js';
import type { ScriptContext } from '../appTypes.js';
import type { RowPreference } from '../model.js';
import { text } from '../resources/pt-BR.js';
import { ScriptList } from './ScriptList.js';

interface Props {
  rows: ScriptSnapshot[];
  selectedId: string | null;
  query: ScriptQuery;
  scriptCount: number;
  readonly: boolean;
  busy: boolean;
  searchRef: React.RefObject<HTMLInputElement | null>;
  pendingPreference: { scriptId: string; field: RowPreference } | null;
  onQuery(patch: Partial<Omit<ScriptQuery, 'version'>>): void;
  onSelect(id: string): void;
  onContext(context: ScriptContext): void;
  onPreference(id: string, field: RowPreference, value: boolean): void;
  onAdd(): void;
}

export function ScriptsPage({
  rows,
  selectedId,
  query,
  scriptCount,
  readonly,
  busy,
  searchRef,
  pendingPreference,
  onQuery,
  onSelect,
  onContext,
  onPreference,
  onAdd,
}: Props): React.ReactElement {
  return (
    <>
      <div className="script-filters">
        <Input
          ref={searchRef}
          contentBefore={<SearchRegular />}
          aria-label={text.search}
          placeholder={text.search}
          value={query.search}
          onChange={(_, data) => onQuery({ search: data.value })}
        />
        <select
          aria-label={text.status}
          value={query.status}
          onChange={(event) => onQuery({ status: event.target.value as ScriptQuery['status'] })}
        >
          <option value="All">{text.allStatus}</option>
          <option value="Running">{text.running}</option>
          <option value="Stopped">{text.stopped}</option>
          <option value="Problems">{text.problems}</option>
        </select>
        <select
          aria-label={text.sort}
          value={query.sort}
          onChange={(event) => onQuery({ sort: event.target.value as ScriptQuery['sort'] })}
        >
          <option value="Name">{text.name}</option>
          <option value="Status">{text.status}</option>
          <option value="Collection">{text.collection}</option>
        </select>
      </div>
      {rows.length ? (
        <ScriptList
          rows={rows}
          selectedId={selectedId}
          onSelect={onSelect}
          onContext={(id, x, y) => {
            onSelect(id);
            onContext({ id, x, y });
          }}
          queryVersion={query.version}
          preferencesDisabled={readonly || busy}
          pendingPreference={pendingPreference}
          onPreference={onPreference}
        />
      ) : (
        <div className="empty-page">
          <DocumentRegular />
          <h2>{scriptCount ? text.noMatches : text.empty}</h2>
          <p>{scriptCount ? text.noMatchesDescription : text.emptyDescription}</p>
          {scriptCount ? (
            <Button onClick={() => onQuery({ search: '', status: 'All', collectionId: null })}>
              {text.clearFilters}
            </Button>
          ) : (
            <Button
              appearance="primary"
              icon={<AddRegular />}
              disabled={readonly || busy}
              onClick={onAdd}
            >
              {text.add}
            </Button>
          )}
        </div>
      )}
    </>
  );
}
