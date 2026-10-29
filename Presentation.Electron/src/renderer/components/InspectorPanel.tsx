import * as React from 'react';
import { Button } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { text } from '../resources/pt-BR.js';
import { Inspector } from './Inspector.js';

interface Props extends React.ComponentProps<typeof Inspector> {
  inspectorRef: React.RefObject<HTMLElement | null>;
  narrow: boolean;
  showDetails: boolean;
  onHideDetails(): void;
}

export function InspectorPanel({
  inspectorRef,
  narrow,
  showDetails,
  onHideDetails,
  ...inspectorProps
}: Props): React.ReactElement {
  return (
    <aside
      ref={inspectorRef}
      className="inspector"
      aria-label={text.details}
      hidden={narrow && !showDetails}
    >
      {narrow && (
        <Button
          className="close-inspector"
          appearance="subtle"
          icon={<DismissRegular />}
          aria-label={text.hideDetails}
          onClick={onHideDetails}
        />
      )}
      <Inspector {...inspectorProps} />
    </aside>
  );
}
