export type FixtureResponse = { value?: unknown; error?: string; pending?: boolean };
type Pending = { resolve(value: unknown): void; reject(error: Error): void };

/** Nonshipping responses, including every request left behind by a failed scenario. */
export class FixtureResponseStore {
  private readonly responses = new Map<string, FixtureResponse>();
  private readonly waiting = new Map<string, Set<Pending>>();

  set(method: string, response: FixtureResponse): void {
    this.responses.set(method, response);
  }

  get(method: string): FixtureResponse | undefined {
    return this.responses.get(method);
  }

  wait(method: string): Promise<unknown> {
    return new Promise((resolve, reject) => {
      let requests = this.waiting.get(method);
      if (!requests) this.waiting.set(method, (requests = new Set()));
      requests.add({ resolve, reject });
    });
  }

  resolve(method: string, response: FixtureResponse): void {
    const requests = this.waiting.get(method);
    if (!requests?.size) throw new Error('No pending fixture response');
    if (requests.size !== 1) throw new Error('Multiple pending fixture responses for ' + method);
    this.waiting.delete(method);
    const pending = requests.values().next().value!;
    if (response.error) pending.reject(new Error(response.error));
    else pending.resolve(response.value);
  }

  reset(): void {
    if (this.waiting.size) throw new Error('Unresolved fixture response');
    this.responses.clear();
  }

  endScenario(): void {
    const requests = [...this.waiting.values()].flatMap((group) => [...group]);
    this.waiting.clear();
    this.responses.clear();
    for (const pending of requests)
      pending.reject(new Error('The isolated scenario ended before its response completed.'));
  }
}
