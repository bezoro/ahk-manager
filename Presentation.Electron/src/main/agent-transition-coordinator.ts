import { failed, type AgentScope } from './agent-commands.js';
import type { AgentReceipt, AgentReceipts, AgentTransition } from './agent-receipts.js';
import type { AgentReply } from './agent-transport.js';

type Dispatch = 'Dispatched' | 'Cancelled' | 'Failed';
type ReceiptStore = Pick<AgentReceipts, 'begin' | 'markDataMoveDispatch' | 'settle'>;

interface AgentTransitionHost {
  generation: string;
  scope(): AgentScope | undefined;
  ready(): boolean;
  owner(): object | null;
  current(owner: object | null): boolean;
  receipts(): ReceiptStore | null;
  review(operationId: string, eligible: () => boolean): Promise<boolean>;
  run(scope: AgentScope, work: () => Promise<Dispatch>): Promise<Dispatch>;
  uncertain(): boolean;
  error(error: unknown): string;
  report(error: unknown): void;
  resume(): void;
}

/** Receipt publication, delivery acknowledgement and dispatch share one transition reservation. */
export class AgentTransitionCoordinator {
  private operationId: string | null = null;

  constructor(private readonly host: AgentTransitionHost) {}

  get active(): string | null {
    return this.operationId;
  }

  async reserve(
    operationId: string,
    transition: AgentTransition,
    work: () => Promise<Dispatch>,
    updateRelease?: string,
  ): Promise<AgentReply> {
    const scope = this.host.scope();
    const receipts = this.host.receipts();
    if (!scope || !receipts || !scope.alive() || !this.host.ready() || this.operationId)
      return failed('OperationBlocked');
    const owner = this.host.owner();
    const eligible = () =>
      scope.alive() && this.host.ready() && this.host.current(owner) && this.operationId === null;
    if (!(await this.host.review(operationId, eligible)) || !eligible())
      return failed('PreflightBlocked');
    this.operationId = operationId;
    let receipt: AgentReceipt;
    try {
      receipt = await receipts.begin(operationId, this.host.generation, transition);
    } catch (error) {
      this.operationId = null;
      throw error;
    }
    let completed = false;
    return {
      outcome: 'Accepted',
      code: 'OperationAccepted',
      result: receipt,
      afterReply: async (delivered) => {
        if (completed) return;
        completed = true;
        let reconciled: Dispatch | 'Unknown' = 'Cancelled';
        let detail: string | undefined;
        try {
          if (delivered && scope.alive() && this.host.current(owner)) {
            if (receipt.kind === 'DataMove') await receipts.markDataMoveDispatch(receipt);
            reconciled = await this.host.run(
              {
                ...scope,
                transition: operationId,
                updateRelease,
                alive: () => this.operationId === operationId && this.host.current(owner),
              },
              work,
            );
          }
        } catch (error) {
          reconciled = this.host.uncertain() ? 'Unknown' : 'Failed';
          detail = this.host.error(error);
        } finally {
          if (reconciled !== 'Dispatched') {
            try {
              await receipts.settle(receipt, reconciled, detail);
            } catch (error) {
              this.host.report(error);
            } finally {
              this.operationId = null;
              this.host.resume();
            }
          }
        }
      },
    };
  }
}
