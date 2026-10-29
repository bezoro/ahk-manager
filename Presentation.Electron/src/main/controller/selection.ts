import type { ManagerSnapshot, SelectionContext } from '../../shared/contracts.js';

export function selectionKey(selection: SelectionContext | null): string {
  return selection
    ? JSON.stringify([
        selection.scriptId,
        selection.query.search,
        selection.query.status,
        selection.query.sort,
        selection.query.collectionId,
        selection.query.version,
      ])
    : '';
}
export function selectionVisible(snapshot: ManagerSnapshot, selection: SelectionContext): boolean {
  const script = snapshot.scripts.find((s) => s.id === selection.scriptId);
  if (!script) return false;
  const query = selection.query;
  if (query.collectionId !== null && script.collectionId !== query.collectionId) return false;
  if (query.status === 'Running' && script.status !== 'Running') return false;
  if (query.status === 'Stopped' && script.status !== 'Stopped') return false;
  if (
    query.status === 'Problems' &&
    !script.issues.length &&
    !['Unknown', 'Multiple', 'Missing', 'Blocked'].includes(script.status)
  )
    return false;
  const search = query.search.trim().toLocaleLowerCase('en-US');
  const description = script.headerDescription?.trim()
    ? script.headerDescription
    : script.description;
  return (
    !search ||
    `${script.friendlyName}\n${script.path}\n${description}`
      .toLocaleLowerCase('en-US')
      .includes(search)
  );
}
