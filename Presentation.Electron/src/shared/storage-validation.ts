import type { StorageChangeResult, StorageState } from './storage.js';
import { bool, nullable, oneOf, shapeFor, text } from './validation.js';

export const storageState = shapeFor<StorageState>()({
  mode: oneOf('portable'),
  currentDataDirectory: text(32760),
  defaultDataDirectory: text(32760),
  pendingDataDirectory: nullable(text(32760)),
  canChange: bool,
  error: nullable(text(4096)),
});

export const storageChangeResult = shapeFor<StorageChangeResult>()({
  state: storageState,
  restartRequired: bool,
});
