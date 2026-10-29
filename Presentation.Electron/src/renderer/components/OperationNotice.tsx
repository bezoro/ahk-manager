import * as React from 'react';
import { Button } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { text } from '../resources/en-US.js';

export interface Notice { title: string; message: string; detail?: string | null; warning: boolean }
export function OperationNotice({ notice, onDismiss, onCopy }: { notice: Notice | null; onDismiss(): void; onCopy?(value: string): void }): React.ReactElement | null {
  if (!notice) return null;
  return <div className={'notice ' + (notice.warning ? 'warning' : 'success')} role={notice.warning ? 'alert' : 'status'} aria-atomic="true"><div><strong>{notice.title}</strong>{notice.message && <p>{notice.message}</p>}{notice.detail && <details><summary>{text.technicalDetails}</summary><pre className="selectable">{notice.detail}</pre>{onCopy && <Button size="small" onClick={() => onCopy(notice.detail!)}>{text.copyDetails}</Button>}</details>}</div><Button size="small" appearance="subtle" icon={<DismissRegular />} aria-label={text.dismiss} onClick={onDismiss} /></div>;
}
