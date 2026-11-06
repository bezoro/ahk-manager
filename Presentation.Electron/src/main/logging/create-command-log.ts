import { CommandLog } from './command-log.js';
import type { DeploymentLayout } from '../storage/deployment.js';

/** Command logs follow the selected portable Data directory. */
export function createCommandLog(deployment: Pick<DeploymentLayout, 'dataDirectory'>): CommandLog {
  return new CommandLog(deployment.dataDirectory);
}
