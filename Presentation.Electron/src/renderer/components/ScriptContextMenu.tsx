import * as React from 'react';
import { Button } from '@fluentui/react-components';
import type { HostState, ScriptOperation, ScriptSnapshot } from '../../shared/contracts.js';
import type { ScriptContext } from '../appTypes.js';
import { actionReason } from '../model.js';
import { operation, reason, text } from '../resources/pt-BR.js';

interface Props {
  host: HostState;
  selected: ScriptSnapshot;
  context: ScriptContext;
  contextRef: React.RefObject<HTMLDivElement | null>;
  busy: boolean;
  readonly: boolean;
  draftDirty: boolean;
  onClose(): void;
  onAction(value: ScriptOperation): void;
  onMetadata(): void;
  onRemove(): void;
}

export function ScriptContextMenu({
  host,
  selected,
  context,
  contextRef,
  busy,
  readonly,
  draftDirty,
  onClose,
  onAction,
  onMetadata,
  onRemove,
}: Props): React.ReactElement {
  return (
    <div
      ref={contextRef}
      role="menu"
      aria-label={text.more}
      className="script-context"
      style={{
        left: Math.min(context.x, window.innerWidth - 244),
        top: Math.min(context.y, window.innerHeight - 365),
      }}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          onClose();
          document.querySelector<HTMLElement>('[role="grid"]')?.focus();
        }
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault();
          const buttons = [
            ...event.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'),
          ];
          const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
          buttons[
            (index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length
          ]?.focus();
        }
      }}
    >
      {(
        [
          'Start',
          'Stop',
          'Restart',
          'TogglePause',
          'ToggleSuspend',
          'Edit',
          'OpenFolder',
          'Recover',
        ] as ScriptOperation[]
      ).map((value) => (
        <Button
          key={value}
          role="menuitem"
          appearance="subtle"
          disabled={busy || !!actionReason(host, selected, value)}
          title={
            actionReason(host, selected, value)
              ? reason(actionReason(host, selected, value)!)
              : operation(value)
          }
          onClick={() => {
            onClose();
            onAction(value);
          }}
        >
          {operation(value)}
        </Button>
      ))}
      <Button role="menuitem" appearance="subtle" onClick={onMetadata}>
        {text.metadata}
      </Button>
      <Button
        role="menuitem"
        appearance="subtle"
        disabled={busy || readonly || draftDirty}
        onClick={() => {
          onClose();
          onRemove();
        }}
      >
        {text.remove}
      </Button>
    </div>
  );
}
