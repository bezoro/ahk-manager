import * as React from 'react';
import { Button, Input, Spinner } from '@fluentui/react-components';
import { CopyRegular, SearchRegular } from '@fluentui/react-icons';
import type { ActivitySnapshot, ManagerSnapshot, Outcome } from '../../shared/contracts.js';
import { activityAssociation } from '../model.js';
import { date } from '../format.js';
import { operation, outcome, outcomes, reason, text } from '../resources/en-US.js';

export function Activity({
  snapshot,
  onCopy,
  onError,
}: {
  snapshot: ManagerSnapshot;
  onCopy(value: string): void;
  onError(error: unknown): void;
}): React.ReactElement {
  const [search, setSearch] = React.useState('');
  const [scriptId, setScriptId] = React.useState('');
  const [result, setResult] = React.useState('');
  const [events, setEvents] = React.useState(snapshot.activity);
  const [loading, setLoading] = React.useState(false);
  React.useEffect(() => {
    let current = true;
    setLoading(true);
    void window.ahkManager
      .getActivity({ scriptId: scriptId || null, outcome: (result || null) as Outcome | null })
      .then((value) => {
        if (current) setEvents(value);
      })
      .catch((error) => {
        if (current) onError(error);
      })
      .finally(() => {
        if (current) setLoading(false);
      });
    return () => {
      current = false;
    };
  }, [snapshot.revision, scriptId, result, onError]);
  const needle = search.toLocaleLowerCase('en-US').trim();
  const rows = events.filter((event) => {
    const association = activityAssociation(event, snapshot.scripts);
    return (
      (!scriptId || event.scriptId === scriptId) &&
      (!result || event.outcome === result) &&
      (!needle ||
        [
          association.name,
          association.path,
          event.detail,
          reason(event.code),
          outcome(event.outcome),
          event.operation ? operation(event.operation) : text.managerActivity,
        ]
          .join(' ')
          .toLocaleLowerCase('en-US')
          .includes(needle))
    );
  });
  const describe = (event: ActivitySnapshot): string => {
    const association = activityAssociation(event, snapshot.scripts);
    return [
      date(event.timestamp),
      association.name || text.managerActivity,
      association.path,
      event.operation ? operation(event.operation) : '',
      outcome(event.outcome),
      reason(event.code),
      event.detail,
    ]
      .filter(Boolean)
      .join('\n');
  };
  return (
    <div className="data-page">
      <div className="page-filters">
        <Input
          contentBefore={<SearchRegular />}
          aria-label={text.searchOther}
          placeholder={text.searchOther}
          value={search}
          onChange={(_, data) => setSearch(data.value)}
        />
        <select
          aria-label={text.script}
          value={scriptId}
          onChange={(event) => setScriptId(event.target.value)}
        >
          <option value="">{text.allScripts}</option>
          {snapshot.scripts.map((script) => (
            <option key={script.id} value={script.id}>
              {script.friendlyName}
            </option>
          ))}
        </select>
        <select
          aria-label={text.result}
          value={result}
          onChange={(event) => setResult(event.target.value)}
        >
          <option value="">{text.allOutcomes}</option>
          {Object.entries(outcomes).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>
      </div>
      <p className="muted">{text.activityHint}</p>
      {loading && <Spinner size="tiny" label={text.loadingActivity} />}
      <div className="page-scroll activity-list">
        {rows.length ? (
          rows.map((event) => {
            const association = activityAssociation(event, snapshot.scripts);
            return (
              <article key={event.id}>
                <div className="activity-heading">
                  <strong>{association.name || text.managerActivity}</strong>
                  <Button
                    icon={<CopyRegular />}
                    appearance="subtle"
                    size="small"
                    aria-label={text.copyDetails}
                    onClick={() => onCopy(describe(event))}
                  />
                </div>
                {association.path && <small className="break-path">{association.path}</small>}
                <p>
                  {event.operation ? operation(event.operation) + ' · ' : ''}
                  {outcome(event.outcome)}
                </p>
                {event.code !== 'None' && <p>{reason(event.code)}</p>}
                <time>{date(event.timestamp)}</time>
                {event.detail && (
                  <details>
                    <summary>{text.technicalDetails}</summary>
                    <pre className="selectable">{event.detail}</pre>
                  </details>
                )}
              </article>
            );
          })
        ) : (
          <div className="empty-page">
            <h2>{text.noActivity}</h2>
          </div>
        )}
      </div>
    </div>
  );
}
