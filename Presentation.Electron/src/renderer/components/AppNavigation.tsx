import * as React from 'react';
import {
  Button,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Tooltip,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  DocumentRegular,
  FolderRegular,
  HistoryRegular,
  InfoRegular,
  KeyboardRegular,
  MoreHorizontalRegular,
  SettingsRegular,
} from '@fluentui/react-icons';
import type { CollectionSnapshot, ScriptQuery } from '../../shared/contracts.js';
import type { CollectionDialogState, Page } from '../appTypes.js';
import { text } from '../resources/en-US.js';

interface Props {
  page: Page;
  query: ScriptQuery;
  collections: CollectionSnapshot[] | undefined;
  availableVersion: string | null;
  readonly: boolean;
  busy: boolean;
  onNavigate(page: Page): void;
  onCollection(id: string): void;
  onOpenCollection(value: CollectionDialogState): void;
  onRemoveCollection(id: string): void;
  onPreferences(): void;
  onHelp(): void;
}

export function AppNavigation({
  page,
  query,
  collections,
  availableVersion,
  readonly,
  busy,
  onNavigate,
  onCollection,
  onOpenCollection,
  onRemoveCollection,
  onPreferences,
  onHelp,
}: Props): React.ReactElement {
  return (
    <nav className="navigation" aria-label={text.library}>
      <div className="nav-primary">
        {(
          [
            { id: 'scripts', label: text.scripts, icon: <DocumentRegular /> },
            { id: 'hotkeys', label: text.hotkeys, icon: <KeyboardRegular /> },
            { id: 'activity', label: text.activity, icon: <HistoryRegular /> },
            { id: 'updates', label: text.updates, icon: <ArrowDownloadRegular /> },
          ] as const
        ).map((item) => (
          <Button
            key={item.id}
            appearance="subtle"
            className={'nav-item ' + (page === item.id ? 'active' : '')}
            icon={item.icon}
            aria-label={item.label}
            aria-description={
              item.id === 'updates' && availableVersion
                ? `${text.updateAvailableVersion} ${availableVersion}`
                : undefined
            }
            title={item.label}
            aria-current={page === item.id ? 'page' : undefined}
            onClick={() => onNavigate(item.id)}
          >
            <span className="nav-label">{item.label}</span>
            {item.id === 'updates' && availableVersion && (
              <span
                className="update-nav-dot"
                role="img"
                aria-label={`${text.updateAvailableVersion} ${availableVersion}`}
              />
            )}
          </Button>
        ))}
      </div>
      <div className="nav-collections">
        <div className="section-heading">
          <span className="nav-heading">{text.collections}</span>
          <Tooltip content={text.createCollection} relationship="label">
            <Button
              size="small"
              appearance="subtle"
              icon={<AddRegular />}
              aria-label={text.createCollection}
              disabled={readonly || busy}
              onClick={() => onOpenCollection({ id: null, name: '' })}
            />
          </Tooltip>
        </div>
        <div className="collection-scroll">
          {collections?.map((collection) => (
            <div className="collection-nav" key={collection.id}>
              <Button
                appearance="subtle"
                className={
                  'nav-item ' +
                  (page === 'scripts' && query.collectionId === collection.id ? 'active' : '')
                }
                icon={<FolderRegular />}
                aria-current={
                  page === 'scripts' && query.collectionId === collection.id ? 'page' : undefined
                }
                onClick={() => onCollection(collection.id)}
              >
                {collection.name}
              </Button>
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button
                    className="collection-options"
                    appearance="subtle"
                    size="small"
                    aria-label={text.manageCollection + ': ' + collection.name}
                    icon={<MoreHorizontalRegular />}
                    disabled={readonly || busy}
                  />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    <MenuItem
                      onClick={() => onOpenCollection({ id: collection.id, name: collection.name })}
                    >
                      {text.renameCollection}
                    </MenuItem>
                    <MenuItem onClick={() => onRemoveCollection(collection.id)}>
                      {text.deleteCollection}
                    </MenuItem>
                  </MenuList>
                </MenuPopover>
              </Menu>
            </div>
          ))}
        </div>
      </div>
      <div className="nav-bottom">
        <Button
          className="nav-item"
          appearance="subtle"
          icon={<SettingsRegular />}
          onClick={onPreferences}
        >
          {text.preferences}
        </Button>
        <Button className="nav-item" appearance="subtle" icon={<InfoRegular />} onClick={onHelp}>
          {text.help}
        </Button>
      </div>
    </nav>
  );
}
