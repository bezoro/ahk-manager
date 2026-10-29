import * as React from 'react';
import type { HostState, ManagerSnapshot, OperationResult } from '../../shared/contracts.js';
import {
  metadataOf,
  newDraft,
  rebaseRowPreference,
  reconcileDraft,
  type Draft,
  type RowPreference,
} from '../model.js';
import { reason, text } from '../resources/pt-BR.js';
import type { Operations } from './useOperations.js';

interface PreferenceAttempt {
  previous: boolean | null;
  requested: boolean;
}

export function useMetadataDrafts(snapshot: ManagerSnapshot | null, operations: Operations) {
  const [drafts, replaceDrafts] = React.useState<Record<string, Draft>>({});
  const state = React.useRef(drafts);
  const [pendingPreference, setPendingPreference] = React.useState<{
    scriptId: string;
    field: RowPreference;
  } | null>(null);
  const readbacks = React.useRef(new Map<string, Map<RowPreference, PreferenceAttempt>>());
  const { run, showNotice, report } = operations;

  // Keep the guard and async completions on the same draft revision as event handlers.
  const setDrafts = React.useCallback(
    (update: React.SetStateAction<Record<string, Draft>>): void => {
      const next = typeof update === 'function' ? update(state.current) : update;
      state.current = next;
      replaceDrafts(next);
    },
    [],
  );

  React.useEffect(() => {
    if (!snapshot) return;
    setDrafts((previous) => {
      const ids = new Set(snapshot.collections.map((collection) => collection.id));
      return Object.fromEntries(
        snapshot.scripts.map((script) => [
          script.id,
          reconcileDraft(previous[script.id], script, ids),
        ]),
      );
    });
  }, [snapshot, setDrafts]);

  const readPreferenceState = async (): Promise<HostState> => {
    await window.ahkManager.refresh();
    const current = await window.ahkManager.getState();
    if (current.connection !== 'ready' || !current.snapshot || current.snapshot.readOnly) {
      throw new Error(text.preferenceReadbackFailed);
    }
    setDrafts((previous) => {
      const next = { ...previous };
      for (const [id, fields] of readbacks.current) {
        const row = current.snapshot!.scripts.find((script) => script.id === id);
        if (!row) continue;
        let draft = previous[id] ?? newDraft(row);
        for (const [field, attempt] of fields) {
          if (attempt.previous !== attempt.requested && row[field] === attempt.requested) {
            draft = rebaseRowPreference(draft, field, row);
          }
        }
        next[id] = draft;
      }
      return next;
    });
    readbacks.current.clear();
    return current;
  };

  const savePreference = (id: string, field: RowPreference, value: boolean): void => {
    void run(async () => {
      setPendingPreference({ scriptId: id, field });
      try {
        const current = readbacks.current.size
          ? await readPreferenceState()
          : await window.ahkManager.getState();
        if (current.connection !== 'ready' || !current.snapshot || current.snapshot.readOnly) {
          throw new Error(text.preferenceReadbackFailed);
        }
        const target = current.snapshot.scripts.find((script) => script.id === id);
        if (!target) throw new Error(reason('StaleSelection'));
        const fields = readbacks.current.get(id) ?? new Map<RowPreference, PreferenceAttempt>();
        fields.set(field, { previous: target[field], requested: value });
        readbacks.current.set(id, fields);

        let result: OperationResult;
        try {
          result = await window.ahkManager.saveMetadata(
            id,
            {
              ...metadataOf(target),
              [field]: value,
            },
            current.snapshot.revision,
          );
        } catch (error) {
          try {
            await readPreferenceState();
          } catch {
            /* Retain unresolved fields until an explicit action can read authority. */
          }
          throw error;
        }
        if (
          result.outcome === 'Confirmed' ||
          result.outcome === 'Partial' ||
          result.outcome === 'Unknown'
        ) {
          const observed = await readPreferenceState();
          const row = observed.snapshot!.scripts.find((script) => script.id === id);
          if (result.outcome === 'Confirmed' && (!row || row[field] !== value)) {
            showNotice({
              title: text.preferenceReadbackFailed,
              message: text.preferenceChanged,
              warning: true,
            });
            return;
          }
        } else {
          fields.delete(field);
          if (!fields.size) readbacks.current.delete(id);
        }
        if (result.outcome === 'CancelledBeforeDispatch') {
          showNotice({ title: text.preferenceCancelled, message: '', warning: false });
        } else report(result);
      } finally {
        setPendingPreference(null);
      }
    });
  };

  const saveDraft = (id: string): void => {
    void run(async () => {
      const current = readbacks.current.size
        ? await readPreferenceState()
        : await window.ahkManager.getState();
      if (
        current.connection !== 'ready' ||
        !current.snapshot ||
        current.snapshot.readOnly ||
        !current.snapshot.scripts.some((script) => script.id === id)
      ) {
        throw new Error(text.preferenceReadbackFailed);
      }
      const draft = state.current[id];
      if (!draft) return;
      const savedValue = JSON.stringify(draft.value);
      const result = await window.ahkManager.saveMetadata(
        id,
        draft.value,
        current.snapshot.revision,
      );
      report(result);
      if (result.outcome === 'Confirmed')
        setDrafts((previous) => {
          const latest = previous[id];
          if (!latest || JSON.stringify(latest.value) !== savedValue) return previous;
          return { ...previous, [id]: { ...latest, base: savedValue, dirty: false } };
        });
    });
  };

  return { drafts, state, setDrafts, pendingPreference, readbacks, savePreference, saveDraft };
}

export type MetadataDrafts = ReturnType<typeof useMetadataDrafts>;
