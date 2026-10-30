import type { StorageChangeResult, StorageState } from '../../shared/storage.js';
import type { DeploymentLayout } from '../storage/deployment.js';
import { cancelPendingMove, getStorageState, prepareDataMove } from '../storage/data-migration.js';

export interface DataLocationBoundary {
  ready(): boolean;
  alive(): boolean;
  quiesce(value: boolean): void;
  drain(): Promise<void>;
  chooseDirectory(current: string): Promise<string | null>;
  preparePrivateDirectory(directory: string): Promise<void>;
  openDirectory(directory: string): Promise<void>;
  restart(): Promise<void>;
}

/** The renderer requests actions; only the native picker can supply a new path. */
export class DataLocationController {
  private running = false;
  constructor(
    readonly layout: DeploymentLayout,
    private error: string | null,
    private readonly boundary: DataLocationBoundary,
  ) {}

  get active(): boolean {
    return this.running;
  }

  get state(): StorageState {
    const state = getStorageState(this.layout, this.error);
    return { ...state, canChange: state.canChange && this.boundary.ready() && !this.running };
  }

  private async mutate<T>(action: () => Promise<T>): Promise<T> {
    if (this.layout.mode !== 'portable')
      throw new Error('Changing the data folder is available in the portable edition.');
    if (this.running || !this.boundary.ready() || !this.boundary.alive())
      throw new Error('Wait for Manager operations to finish before changing the data location.');
    this.running = true;
    this.boundary.quiesce(true);
    try {
      await this.boundary.drain();
      if (!this.boundary.alive()) throw new Error('The application page changed. Try again.');
      return await action();
    } finally {
      this.running = false;
      this.boundary.quiesce(false);
    }
  }

  private async prepare(directory: string): Promise<StorageChangeResult> {
    const result = await prepareDataMove(
      this.layout,
      directory,
      this.boundary.preparePrivateDirectory,
    );
    this.error = null;
    return result;
  }

  choose = (): Promise<StorageChangeResult | null> =>
    this.mutate(async () => {
      const directory = await this.boundary.chooseDirectory(this.layout.dataDirectory);
      if (!directory) return null;
      if (!this.boundary.alive()) throw new Error('The application page changed. Try again.');
      return this.prepare(directory);
    });

  restoreDefault = (): Promise<StorageChangeResult> =>
    this.mutate(() => this.prepare(this.layout.defaultDataDirectory));

  cancel = (): Promise<StorageState> =>
    this.mutate(async () => {
      const state = await cancelPendingMove(this.layout);
      this.error = null;
      return state;
    });

  open = async (): Promise<void> => {
    if (!this.boundary.alive()) throw new Error('The application page changed. Try again.');
    await this.boundary.openDirectory(this.layout.dataDirectory);
  };

  restart = (): Promise<void> =>
    this.mutate(async () => {
      if (!getStorageState(this.layout).pendingDataDirectory) return;
      await this.boundary.restart();
    });
}
