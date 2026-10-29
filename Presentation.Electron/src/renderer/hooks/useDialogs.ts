import * as React from 'react';
import type { CollectionDialogState, DialogSession } from '../appTypes.js';
import type { Notice } from '../components/OperationNotice.js';

export function useDialogs() {
  const [preferences, setPreferences] = React.useState(false);
  const [help, setHelp] = React.useState(false);
  const [collection, setCollection] = React.useState<CollectionDialogState | null>(null);
  const [preview, setPreview] =
    React.useState<Awaited<ReturnType<typeof window.ahkManager.previewImport>>>(null);
  const [notice, setNotice] = React.useState<Notice | null>(null);
  const session = React.useRef<DialogSession | null>(null);

  const begin = React.useCallback((kind: DialogSession['kind']): DialogSession => {
    const next = { kind };
    session.current = next;
    setNotice(null);
    setHelp(false);
    setPreferences(false);
    setCollection(null);
    setPreview(null);
    return next;
  }, []);

  const close = React.useCallback((): void => {
    session.current = null;
    setNotice(null);
    setPreferences(false);
    setCollection(null);
    setPreview(null);
  }, []);

  const openPreferences = React.useCallback((): void => {
    if (session.current?.kind === 'preferences') return;
    begin('preferences');
    setPreferences(true);
  }, [begin]);

  const openCollection = (value: CollectionDialogState): void => {
    begin('collection');
    setCollection(value);
  };

  React.useEffect(() => window.ahkManager.onOpenPreferences(openPreferences), [openPreferences]);

  return {
    preferences,
    help,
    collection,
    preview,
    notice,
    session,
    open: preferences || help || !!preview || !!collection,
    begin,
    close,
    openPreferences,
    openCollection,
    setHelp,
    setPreview,
    setNotice,
  };
}

export type Dialogs = ReturnType<typeof useDialogs>;
