import type { ScriptFileIdentity, WindowPlacement } from '../../shared/contracts.js';

export interface Authority {
  request<T>(action: string, payload: unknown): Promise<T>;
}
export interface NativeBoundary {
  files(
    kind: 'scripts' | 'locate' | 'folder' | 'interpreter' | 'editor' | 'import' | 'migration',
  ): Promise<string[]>;
  confirm(title: string, detail: string): Promise<boolean>;
  placement(): WindowPlacement;
  alive(): boolean;
  fileStamp?(file: ScriptFileIdentity): Promise<string>;
  trash?(path: string): Promise<void>;
}
