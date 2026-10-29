import { hostText } from '../../shared/host-pt-BR.js';

export interface RequestPolicy {
  quitting: boolean;
  updateApplying: boolean;
  adoptionPending: boolean;
  administrationPending: boolean;
}

const duringShutdown = new Set(['getState', 'getTheme']);
const duringUpdate = new Set(['getState', 'getTheme', 'getUpdateState']);
const duringAdoption = new Set(['getState', 'getTheme', 'getActivity']);
const duringAdministration = new Set([...duringAdoption, 'requestAdministration']);

/** These checks run after document authentication and before dispatching any action. */
export function assertRequestAllowed(name: string, state: RequestPolicy): void {
  if (state.quitting && !duringShutdown.has(name)) throw new Error(hostText.closing);
  if (state.updateApplying && !duringUpdate.has(name)) {
    throw new Error('A instalação da atualização está sendo preparada.');
  }
  if (state.adoptionPending && !duringAdoption.has(name)) {
    throw new Error(hostText.administrationPending);
  }
  if (state.administrationPending && !duringAdministration.has(name)) {
    throw new Error(hostText.administrationPending);
  }
}
