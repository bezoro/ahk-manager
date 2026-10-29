import type {
  HostState,
  ManagerSnapshot,
  OperationResult,
  SelectionContext,
} from '../../shared/contracts.js';
import type { PreparedOperation } from '../backend.js';
import { hostText as t } from '../../shared/host-en-US.js';
import { withinDeadline } from '../lifecycle.js';
import type { Authority, NativeBoundary } from './boundaries.js';
import { sameProcessIdentity } from './identity.js';
import { readySnapshot, rowProblem, trustworthy } from './policies.js';
import { selectionKey } from './selection.js';

export interface StopExitItem {
  scriptId: string;
  name: string;
  path: string;
  pid: number | null;
  outcome: 'alreadyStopped' | 'confirmed' | 'untouched' | 'unknown';
  reason: string;
}
export interface StopExitReceipt {
  canExit: boolean;
  cancelled: boolean;
  expired: boolean;
  confirmedStops: number;
  items: StopExitItem[];
  problems: string[];
}
export interface StopExitOptions {
  cancelled(): boolean;
  now?(): number;
  progress?: (completed: number, total: number) => void;
  finalObservation?: (
    request: () => Promise<ManagerSnapshot>,
    milliseconds: number,
  ) => Promise<ManagerSnapshot>;
}

export interface StopExitBoundary {
  state(): HostState;
  authority(): Authority;
  setSnapshot(snapshot: ManagerSnapshot): void;
  drain(): Promise<void>;
  native: Pick<NativeBoundary, 'alive' | 'confirm'>;
  activeChanged(active: boolean): void;
}

/** Coordinates one catalog-wide Stop batch; the controller still owns queue admission. */
export class StopExitCoordinator {
  private running = false;
  private version = 0n;
  constructor(private readonly boundary: StopExitBoundary) {}
  get active(): boolean {
    return this.running;
  }

  /** Native host only: never called by renderer IPC and independent of its selected row. */
  async run(options: StopExitOptions): Promise<StopExitReceipt> {
    const receipt: StopExitReceipt = {
      canExit: false,
      cancelled: false,
      expired: false,
      confirmedStops: 0,
      items: [],
      problems: [],
    };
    if (this.boundary.state().administrationPending || this.running) {
      receipt.problems.push('BatchBusy');
      return receipt;
    }
    this.running = true;
    this.boundary.activeChanged(true);
    const now = options.now ?? (() => performance.now());
    const cancelled = () => {
      if (options.cancelled() || !this.boundary.native.alive()) {
        receipt.cancelled = true;
        return true;
      }
      return false;
    };
    const observe = async () => {
      readySnapshot(this.boundary.state());
      const fresh = await this.boundary.authority().request<ManagerSnapshot>('refresh', {});
      readySnapshot(this.boundary.state());
      this.boundary.setSnapshot(fresh);
      return fresh;
    };
    try {
      // Prior work, initial observation and native consent do not spend the dispatch budget.
      await this.boundary.drain();
      if (cancelled()) return receipt;
      const initial = await observe();
      if (cancelled()) return receipt;
      if (!trustworthy(initial)) {
        receipt.problems.push('ObservationUntrusted');
        return receipt;
      }
      const cohort = initial.scripts.map((script) => ({
        id: script.id,
        path: script.path,
        name: script.friendlyName,
        identity: script.process ? { ...script.process.identity } : null,
      }));
      const sameScope = (state: ManagerSnapshot) =>
        state.scripts.length === cohort.length &&
        cohort.every((original) =>
          state.scripts.some(
            (script) =>
              script.id === original.id &&
              script.path.toLowerCase() === original.path.toLowerCase() &&
              script.friendlyName === original.name,
          ),
        );
      const approved: typeof cohort = [];
      for (const script of initial.scripts) {
        const problem = rowProblem(script);
        receipt.items.push({
          scriptId: script.id,
          name: script.friendlyName,
          path: script.path,
          pid: script.process?.identity.pid ?? null,
          outcome: problem
            ? 'untouched'
            : script.status === 'Stopped'
              ? 'alreadyStopped'
              : 'untouched',
          reason: problem ?? (script.status === 'Stopped' ? 'None' : 'NotDispatched'),
        });
        if (!problem && script.status === 'Running')
          approved.push(cohort.find((item) => item.id === script.id)!);
      }
      let incomplete = receipt.items.some(
        (item) => item.reason !== 'None' && item.reason !== 'NotDispatched',
      );
      options.progress?.(0, approved.length);
      if (approved.length) {
        const detail = t.stopExitConsent(receipt.items);
        if (detail.length > 64000) {
          receipt.problems.push('ConfirmationTooLarge');
          return receipt;
        }
        if (!(await this.boundary.native.confirm(t.stopExitTitle, detail))) {
          receipt.cancelled = true;
          return receipt;
        }
        if (cancelled()) return receipt;
      }
      const deadline = now() + 60000,
        version = (++this.version).toString();
      let completed = 0;
      for (const original of approved) {
        const item = receipt.items.find((row) => row.scriptId === original.id)!;
        if (cancelled()) break;
        if (now() >= deadline) {
          receipt.expired = true;
          break;
        }
        const fresh = await observe();
        if (cancelled()) break;
        if (!trustworthy(fresh) || !sameScope(fresh)) {
          incomplete = true;
          receipt.problems.push('ScopeOrAuthorityChanged');
          break;
        }
        if (now() >= deadline) {
          receipt.expired = true;
          break;
        }
        const target = fresh.scripts.find((script) => script.id === original.id)!;
        if (!rowProblem(target) && target.status === 'Stopped' && target.process === null) {
          item.outcome = 'alreadyStopped';
          item.reason = 'None';
          options.progress?.(++completed, approved.length);
          continue;
        }
        if (
          rowProblem(target) ||
          !sameProcessIdentity(original.identity, target.process?.identity ?? null)
        ) {
          incomplete = true;
          item.reason = rowProblem(target) ?? 'IdentityChanged';
          options.progress?.(++completed, approved.length);
          continue;
        }
        const selection: SelectionContext = {
          scriptId: original.id,
          stateRevision: fresh.revision,
          query: { search: '', status: 'All', sort: 'Name', collectionId: null, version },
        };
        const prepared = await this.boundary
          .authority()
          .request<PreparedOperation>('prepare', { selection, operation: 'Stop' });
        // Only known Stop tokens requiring confirmation are safe to discard this way.
        if (prepared.plan.operation !== 'Stop' || !prepared.plan.requiresConfirmation) {
          incomplete = true;
          item.reason = 'UnexpectedPlan';
          break;
        }
        const discard = async () => {
          try {
            await this.boundary.authority().request('perform', {
              planId: prepared.planId,
              currentSelection: prepared.plan.selection,
              confirmed: false,
            });
          } catch {
            incomplete = true;
            receipt.problems.push('DiscardUnconfirmed');
          }
        };
        if (cancelled()) {
          await discard();
          break;
        }
        if (deadline - now() < 30000) {
          receipt.expired = true;
          await discard();
          break;
        }
        if (this.boundary.state().connection !== 'ready' || !this.boundary.state().snapshot) {
          incomplete = true;
          item.reason = 'EffectOwnerUnavailable';
          await discard();
          break;
        }
        const current = readySnapshot(this.boundary.state());
        const matches =
          prepared.plan.allowed &&
          prepared.plan.scriptId === original.id &&
          prepared.plan.stateRevision === fresh.revision &&
          selectionKey(prepared.plan.selection) === selectionKey(selection) &&
          prepared.plan.selection.stateRevision === selection.stateRevision &&
          sameProcessIdentity(original.identity, prepared.plan.expectedIdentity) &&
          trustworthy(current) &&
          sameScope(current) &&
          current.revision === fresh.revision &&
          sameProcessIdentity(
            original.identity,
            current.scripts.find((script) => script.id === original.id)?.process?.identity ?? null,
          );
        if (!matches) {
          incomplete = true;
          item.reason = prepared.plan.allowed ? 'StaleSelection' : prepared.plan.reason;
          await discard();
          options.progress?.(++completed, approved.length);
          continue;
        }
        try {
          const result = await this.boundary.authority().request<OperationResult>('perform', {
            planId: prepared.planId,
            currentSelection: selection,
            confirmed: true,
          });
          if (result.outcome === 'Confirmed' && result.code === 'None') {
            item.outcome = 'confirmed';
            item.reason = 'None';
            receipt.confirmedStops++;
          } else {
            incomplete = true;
            item.outcome = 'unknown';
            item.reason = result.code === 'None' ? result.outcome : result.code;
          }
        } catch (error) {
          incomplete = true;
          item.outcome = 'unknown';
          item.reason = error instanceof Error ? error.message : 'Unknown';
        }
        options.progress?.(++completed, approved.length);
        if (now() > deadline) {
          receipt.expired = true;
          break;
        }
        if (cancelled()) break;
      }
      // Cancellation/expiry/uncertain effects are sticky even if late observation looks stopped.
      const final = await (options.finalObservation ?? withinDeadline)(observe, 30000);
      if (cancelled()) incomplete = true;
      const allStopped =
        trustworthy(final) &&
        sameScope(final) &&
        final.scripts.every(
          (script) => script.status === 'Stopped' && script.process === null && !rowProblem(script),
        );
      if (!allStopped) receipt.problems.push('FinalStateNotStopped');
      receipt.canExit =
        allStopped &&
        !incomplete &&
        !receipt.cancelled &&
        !receipt.expired &&
        receipt.items.every(
          (item) => item.outcome === 'confirmed' || item.outcome === 'alreadyStopped',
        );
      return receipt;
    } catch (error) {
      receipt.problems.push(error instanceof Error ? error.message : 'Unknown');
      return receipt;
    } finally {
      this.running = false;
      this.boundary.activeChanged(false);
    }
  }
}
