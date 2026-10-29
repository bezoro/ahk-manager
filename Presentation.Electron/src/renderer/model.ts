import type {
  ActivitySnapshot,
  HostState,
  ManagerSnapshot,
  ScriptMetadata,
  ScriptOperation,
  ScriptQuery,
  ScriptSnapshot,
} from '../shared/contracts.js';

export const defaultQuery = (): ScriptQuery => ({
  search: '',
  status: 'All',
  sort: 'Name',
  collectionId: null,
  version: '0',
});
export const nextQuery = (
  query: ScriptQuery,
  patch: Partial<Omit<ScriptQuery, 'version'>>,
): ScriptQuery => ({ ...query, ...patch, version: (BigInt(query.version) + 1n).toString() });
export const effectiveDescription = (script: ScriptSnapshot): string =>
  script.headerDescription?.trim() ? script.headerDescription : script.description;
export function visibleScripts(snapshot: ManagerSnapshot, query: ScriptQuery): ScriptSnapshot[] {
  const search = query.search.trim().toLocaleLowerCase('en-US');
  const names = new Map(snapshot.collections.map((c) => [c.id, c.name]));
  return snapshot.scripts
    .filter(
      (script) =>
        (!query.collectionId || script.collectionId === query.collectionId) &&
        (!search ||
          `${script.friendlyName} ${script.path} ${effectiveDescription(script)}`
            .toLocaleLowerCase('en-US')
            .includes(search)) &&
        (query.status === 'All' ||
          (query.status === 'Running' && script.status === 'Running') ||
          (query.status === 'Stopped' && script.status === 'Stopped') ||
          (query.status === 'Problems' &&
            (script.issues.length > 0 || !['Stopped', 'Running'].includes(script.status)))),
    )
    .sort((a, b) => {
      const first =
        query.sort === 'Status'
          ? a.status.localeCompare(b.status)
          : query.sort === 'Collection'
            ? (names.get(a.collectionId ?? '') ?? '').localeCompare(
                names.get(b.collectionId ?? '') ?? '',
                'en-US',
              )
            : 0;
      return (
        first ||
        a.friendlyName.localeCompare(b.friendlyName, 'en-US', {
          sensitivity: 'base',
          numeric: true,
        }) ||
        a.id.localeCompare(b.id)
      );
    });
}
export const fileName = (path: string): string => path.split(/[\\/]/).pop() ?? path;
export const metadataOf = (script: ScriptSnapshot): ScriptMetadata => ({
  friendlyName: script.friendlyName,
  description: script.description,
  collectionId: script.collectionId,
  runAtLogin: script.runAtLogin,
  runAsAdministrator: script.runAsAdministrator,
  launchBlocked: script.launchBlocked,
  manualHotkeys: script.hotkeys.filter((key) => key.origin === 'Manual').map((key) => key.chord),
});
export interface Draft {
  value: ScriptMetadata;
  base: string;
  dirty: boolean;
  manualText: string;
}
export type RowPreference = 'runAtLogin' | 'runAsAdministrator';
export function rebaseRowPreference(
  draft: Draft,
  field: RowPreference,
  authoritative: ScriptSnapshot,
): Draft {
  const base = { ...(JSON.parse(draft.base) as ScriptMetadata), [field]: authoritative[field] };
  const value = { ...draft.value, [field]: authoritative[field] };
  return {
    ...draft,
    value,
    base: JSON.stringify(base),
    dirty: JSON.stringify(value) !== JSON.stringify(base),
  };
}
export const newDraft = (script: ScriptSnapshot): Draft => {
  const value = metadataOf(script);
  return {
    value,
    base: JSON.stringify(value),
    dirty: false,
    manualText: value.manualHotkeys.join('\n'),
  };
};
export const changeDraft = (draft: Draft, patch: Partial<ScriptMetadata>): Draft => {
  const value = { ...draft.value, ...patch };
  return {
    ...draft,
    value,
    manualText: patch.manualHotkeys ? patch.manualHotkeys.join('\n') : draft.manualText,
    dirty: JSON.stringify(value) !== draft.base,
  };
};
export const changeManualDraft = (draft: Draft, manualText: string): Draft => ({
  ...changeDraft(draft, {
    manualHotkeys: manualText
      .split(/\r?\n/)
      .map((value) => value.trim())
      .filter(Boolean),
  }),
  manualText,
});
export function reconcileDraft(
  draft: Draft | undefined,
  script: ScriptSnapshot,
  collectionIds: ReadonlySet<string>,
): Draft {
  if (!draft) return newDraft(script);
  if (!draft.dirty)
    return draft.base === JSON.stringify(metadataOf(script)) ? draft : newDraft(script);
  if (draft.value.collectionId && !collectionIds.has(draft.value.collectionId))
    return changeDraft(draft, { collectionId: null });
  return draft;
}
export function actionReason(
  host: HostState,
  script: ScriptSnapshot | null,
  action: ScriptOperation,
): string | null {
  if (host.connection !== 'ready' || !host.snapshot) return 'ObservationIncomplete';
  if (host.administrationPending) return 'PendingOperation';
  if (!script) return 'StaleSelection';
  if (action === 'Edit' || action === 'OpenFolder' || action === 'Recover') return null;
  if (host.snapshot.readOnly) return 'ReadOnlySettings';
  if (!host.snapshot.effectsAvailable) return 'EffectOwnerUnavailable';
  const launchBarrier = !script.runAsAdministrator
    ? (host.snapshot.normalLaunchUnavailableReason ?? null)
    : null;
  if (action === 'Start')
    return script.status !== 'Stopped'
      ? script.status === 'Running'
        ? 'AlreadyRunning'
        : 'ObservationIncomplete'
      : script.launchBlocked
        ? 'LegacyGuard'
        : launchBarrier;
  if (script.status !== 'Running' || !script.process)
    return script.status === 'Multiple' ? 'MultipleInstances' : 'ObservationIncomplete';
  if (!script.process.windowVerified) return 'WindowUnverified';
  if (script.process.controlUnavailableReason) return script.process.controlUnavailableReason;
  if (action === 'Restart' && launchBarrier) return launchBarrier;
  if (action === 'ToggleSuspend' && script.process.suspended === null) return 'StateNotObservable';
  return null;
}
export function activityAssociation(
  event: ActivitySnapshot,
  scripts: readonly ScriptSnapshot[],
): { name: string; path: string } {
  if (!event.scriptId) return { name: '', path: '' };
  const script = scripts.find((item) => item.id === event.scriptId);
  return script
    ? { name: script.friendlyName, path: script.path }
    : { name: event.scriptId, path: '' };
}
export function filterActivity(
  events: readonly ActivitySnapshot[],
  scripts: readonly ScriptSnapshot[],
  search: string,
  scriptId: string | null,
  result: string,
): ActivitySnapshot[] {
  const needle = search.toLocaleLowerCase('en-US').trim();
  return events.filter(
    (event) =>
      (!scriptId || event.scriptId === scriptId) &&
      (!result || event.outcome === result) &&
      (!needle ||
        Object.values({ ...event, ...activityAssociation(event, scripts) })
          .join(' ')
          .toLocaleLowerCase('en-US')
          .includes(needle)),
  );
}
export const rowWindow = (
  length: number,
  scroll: number,
  viewport: number,
  rowHeight = 68,
): { start: number; end: number } => ({
  start: Math.max(0, Math.min(length, Math.floor(Math.max(0, scroll - 38) / rowHeight) - 5)),
  end: Math.min(length, Math.ceil((Math.max(0, scroll - 38) + viewport) / rowHeight) + 6),
});
export function brandPalette(
  accent: string,
): Parameters<typeof import('@fluentui/react-components').createLightTheme>[0] {
  const match = /^#?([a-f\d]{6})(?:[a-f\d]{2})?$/i.exec(accent);
  const value = match?.[1] ?? '737373';
  const rgb = [0, 2, 4].map((i) => Number.parseInt(value.slice(i, i + 2), 16));
  return Object.fromEntries(
    Array.from({ length: 16 }, (_, index) => {
      const shade = (index + 1) * 10;
      const mix = shade <= 80 ? (80 - shade) / 85 : (shade - 80) / 90;
      const target = shade <= 80 ? 0 : 255;
      return [
        shade,
        '#' +
          rgb
            .map((channel) =>
              Math.round(channel + (target - channel) * mix)
                .toString(16)
                .padStart(2, '0'),
            )
            .join(''),
      ];
    }),
  ) as Parameters<typeof import('@fluentui/react-components').createLightTheme>[0];
}
