import { hostText } from '../../shared/host-en-US.js';

export interface RequestPolicy {
  quitting: boolean;
  updateApplying: boolean;
  adoptionPending: boolean;
  administrationPending: boolean;
}

const duringShutdown = new Set(['getState', 'getTheme', 'storage:get']);
const duringUpdate = new Set([
  'getState',
  'getTheme',
  'getUpdateState',
  'storage:get',
  'openLogs',
  'copyDiagnostics',
]);
const duringAdoption = new Set([
  'getState',
  'getTheme',
  'getActivity',
  'storage:get',
  'openLogs',
  'copyDiagnostics',
]);
const duringAdministration = new Set([...duringAdoption, 'requestAdministration']);

/** These checks run after document authentication and before dispatching any action. */
export function assertRequestAllowed(name: string, state: RequestPolicy): void {
  if (state.quitting && !duringShutdown.has(name)) throw new Error(hostText.closing);
  if (state.updateApplying && !duringUpdate.has(name)) {
    throw new Error('The update installation is being prepared.');
  }
  if (state.adoptionPending && !duringAdoption.has(name)) {
    throw new Error(hostText.administrationPending);
  }
  if (state.administrationPending && !duringAdministration.has(name)) {
    throw new Error(hostText.administrationPending);
  }
}
