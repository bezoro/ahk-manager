import type { HostState, ScriptQuery } from '../shared/contracts.js';

export type Page = 'scripts' | 'hotkeys' | 'activity' | 'updates';

export interface DialogSession {
  kind: 'preferences' | 'collection' | 'import';
}

export interface CollectionDialogState {
  id: string | null;
  name: string;
}

export interface ScriptContext {
  id: string;
  x: number;
  y: number;
}

export interface DeleteScope {
  scriptId: string;
  path: string;
  query: ScriptQuery;
  authorityMode: HostState['authorityMode'];
}
