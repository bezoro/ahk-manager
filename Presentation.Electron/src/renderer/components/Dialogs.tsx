import * as React from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
} from '@fluentui/react-components';
import type { DiagnosticExportResult, ImportPreview } from '../../shared/contracts.js';
import { importedCount, reason, text } from '../resources/en-US.js';
import { OperationNotice, type Notice } from './OperationNotice.js';
type Feedback = { notice: Notice | null; onDismissNotice(): void };

export function CollectionDialog({
  initialName,
  rename,
  busy,
  notice,
  onDismissNotice,
  onClose,
  onSave,
}: {
  initialName: string;
  rename: boolean;
  busy: boolean;
  onClose(): void;
  onSave(name: string): void;
} & Feedback): React.ReactElement {
  const [name, setName] = React.useState(initialName);
  return (
    <Dialog
      open
      onOpenChange={(_, data) => {
        if (!data.open && !busy) onClose();
      }}
    >
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{rename ? text.renameCollection : text.createCollection}</DialogTitle>
          <DialogContent>
            <OperationNotice notice={notice} onDismiss={onDismissNotice} />
            <form
              id="collection-form"
              onSubmit={(event) => {
                event.preventDefault();
                if (name.trim() && !busy) onSave(name.trim());
              }}
            >
              <label htmlFor="collection-name">{text.collectionName}</label>
              <Input
                autoFocus
                id="collection-name"
                value={name}
                maxLength={120}
                onChange={(_, data) => setName(data.value)}
                disabled={busy}
              />
              <p className="muted">{text.collectionNameHint}</p>
            </form>
          </DialogContent>
          <DialogActions>
            <Button disabled={busy} onClick={onClose}>
              {text.cancel}
            </Button>
            <Button
              type="submit"
              form="collection-form"
              appearance="primary"
              disabled={busy || !name.trim()}
            >
              {text.save}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function ImportDialog({
  preview,
  busy,
  notice,
  onDismissNotice,
  onClose,
  onCommit,
}: {
  preview: ImportPreview;
  busy: boolean;
  onClose(): void;
  onCommit(): void;
} & Feedback): React.ReactElement {
  return (
    <Dialog
      open
      onOpenChange={(_, data) => {
        if (!data.open && !busy) onClose();
      }}
    >
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{text.importPreview}</DialogTitle>
          <DialogContent>
            <OperationNotice notice={notice} onDismiss={onDismissNotice} />
            <h3>{text.importSource}</h3>
            <p className="selectable file-value">{preview.sourcePath}</p>
            <p>{importedCount(preview.scriptCount)}</p>
            <p>{text.importHint}</p>
            {preview.issues.map((issue, index) => (
              <p key={issue.code + index}>
                {reason(issue.code)}
                {issue.detail && <span className="diagnostic selectable">{issue.detail}</span>}
              </p>
            ))}
          </DialogContent>
          <DialogActions>
            <Button disabled={busy} onClick={onClose}>
              {text.cancel}
            </Button>
            <Button appearance="primary" disabled={busy || !preview.canImport} onClick={onCommit}>
              {text.import}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function HelpDialog({
  busy,
  onOpenLogs,
  onCopyDiagnostics,
  onExportDiagnostics,
  onClose,
  onImport,
  onBackup,
}: {
  busy: boolean;
  onOpenLogs(): Promise<void>;
  onCopyDiagnostics(mode: 'recent' | 'errors'): Promise<void>;
  onExportDiagnostics(): Promise<DiagnosticExportResult | null>;
  onClose(): void;
  onImport(): void;
  onBackup(): void;
}): React.ReactElement {
  const [logsNotice, setLogsNotice] = React.useState<Notice | null>(null);
  const [logsPending, setLogsPending] = React.useState(false);
  const logsRequest = React.useRef(false);
  const diagnosticAction = async (
    action: 'open' | 'recent' | 'errors' | 'export',
  ): Promise<void> => {
    if (logsRequest.current) return;
    logsRequest.current = true;
    setLogsPending(true);
    setLogsNotice(null);
    try {
      if (action === 'open') await onOpenLogs();
      else if (action === 'export') {
        const result = await onExportDiagnostics();
        if (result)
          setLogsNotice({
            title: result.complete
              ? text.diagnosticArchiveSavedTitle
              : text.diagnosticArchivePartialTitle,
            message: result.complete ? text.diagnosticArchiveSaved : text.diagnosticArchivePartial,
            warning: !result.complete,
          });
      } else {
        await onCopyDiagnostics(action);
        setLogsNotice({
          title: text.copied,
          message: text.diagnosticReportCopied,
          warning: false,
        });
      }
    } catch (error) {
      setLogsNotice({
        title: text.error,
        message:
          action === 'open'
            ? text.openLogsFailed
            : action === 'export'
              ? text.exportDiagnosticsFailed
              : text.copyDiagnosticsFailed,
        detail: error instanceof Error ? error.message : String(error),
        warning: true,
      });
    } finally {
      logsRequest.current = false;
      setLogsPending(false);
    }
  };
  return (
    <Dialog
      open
      onOpenChange={(_, data) => {
        if (!data.open) onClose();
      }}
    >
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{text.help}</DialogTitle>
          <DialogContent>
            <p>{text.helpDescription}</p>
            <p>{text.safeEditHint}</p>
            <h3>{text.diagnosticLogs}</h3>
            <p>{text.diagnosticLogsHint}</p>
            <p>{text.exportDiagnosticsHint}</p>
            <p className="muted">{text.copyDiagnosticsHint}</p>
            <OperationNotice notice={logsNotice} onDismiss={() => setLogsNotice(null)} />
            <div className="diagnostic-actions" aria-busy={logsPending}>
              <Button
                appearance="primary"
                disabled={logsPending}
                onClick={() => void diagnosticAction('export')}
              >
                {text.saveDiagnosticArchive}
              </Button>
              <Button disabled={logsPending} onClick={() => void diagnosticAction('recent')}>
                {text.copyDiagnosticReport}
              </Button>
              <Button disabled={logsPending} onClick={() => void diagnosticAction('errors')}>
                {text.copyRecentErrors}
              </Button>
              <Button disabled={logsPending} onClick={() => void diagnosticAction('open')}>
                {text.openLogs}
              </Button>
            </div>
            <h3>{text.shortcuts}</h3>
            <dl className="key-values">
              <dt>Ctrl + F</dt>
              <dd>{text.shortcutSearch}</dd>
              <dt>F5</dt>
              <dd>{text.shortcutRefresh}</dd>
              <dt>Ctrl + N</dt>
              <dd>{text.shortcutAdd}</dd>
              <dt>Shift + F10</dt>
              <dd>{text.shortcutMenu}</dd>
            </dl>
            <h3>{text.importLegacy}</h3>
            <p>{text.importHint}</p>
            <Button disabled={busy} onClick={onImport}>
              {text.importLegacy}
            </Button>
            <p>{text.migrationHint}</p>
            <Button disabled title={text.migrationFirstUseOnly}>
              {text.migrateProfile}
            </Button>
            <p>{text.migrationFirstUseOnly}</p>
            <p>{text.backupHint}</p>
            <Button disabled={busy} onClick={onBackup}>
              {text.restoreBackup}
            </Button>
            <p>{text.helpData}</p>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{text.close}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
