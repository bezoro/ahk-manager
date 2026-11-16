import type { ShutdownBoundary } from '../lifecycle.js';

interface ShutdownBackend {
  readonly exited: boolean;
  shutdown(): Promise<unknown>;
}

interface ShutdownHost {
  backend(): ShutdownBackend | null;
  drain(): Promise<void>;
  persistPlacement(administrative: boolean): Promise<void>;
}

export interface ShutdownOperation {
  readonly dispatched: boolean;
  readonly boundary: ShutdownBoundary;
}

/** Each attempt retains its backend owner; an expired attempt cannot close a replacement. */
export class ShutdownCoordinator {
  private pending: Promise<void> | null = null;

  constructor(private readonly host: ShutdownHost) {}

  get inFlight(): Promise<void> | null {
    return this.pending;
  }

  operation(
    placement: 'standard' | 'administrative' | 'skip',
    reportPlacementFailure: (error: unknown) => void = () => {},
  ): ShutdownOperation {
    const owner = this.host.backend();
    let dispatched = false;
    const current = () => {
      if (this.host.backend() !== owner) throw new Error('The backend changed before shutdown.');
    };
    return {
      get dispatched() {
        return dispatched;
      },
      boundary: {
        drain: async () => {
          await this.host.drain();
          current();
        },
        backendPresent: () => {
          current();
          return owner !== null;
        },
        backendExited: () => {
          current();
          return owner?.exited ?? true;
        },
        persistPlacement: async () => {
          current();
          if (placement !== 'skip')
            await this.host.persistPlacement(placement === 'administrative');
          current();
        },
        shutdownBackend: async () => {
          current();
          if (!owner) return;
          if (dispatched || this.pending)
            throw new Error('Backend shutdown has already been dispatched.');
          dispatched = true;
          const closing = Promise.resolve()
            .then(() => owner.shutdown())
            .then(() => {});
          this.pending = closing;
          try {
            await closing;
          } finally {
            if (this.pending === closing) this.pending = null;
          }
        },
        reportPlacementFailure,
      },
    };
  }
}
