import { bool, nullable, oneOf, shape, text } from './validation.js';

export const storageState = shape({
  mode: oneOf('installed', 'portable'),
  currentDataDirectory: text(32760),
  defaultDataDirectory: text(32760),
  pendingDataDirectory: nullable(text(32760)),
  canChange: bool,
  error: nullable(text(4096)),
});

export const storageChangeResult = shape({
  state: storageState,
  restartRequired: bool,
});
