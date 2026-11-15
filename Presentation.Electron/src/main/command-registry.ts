
import { requireValid, type Validator } from '../shared/validation.js';

export class CommandRegistry {
  private readonly commands = new Map<string, (body: unknown) => () => unknown>();

  define<T>(name: string, validate: Validator, handler: (body: T) => unknown): void {
    if (this.commands.has(name)) throw new Error('Command already registered: ' + name);
    this.commands.set(name, (body) => {
      const value = requireValid<T>(body, validate, name + ' request');
      return () => handler(value);
    });
  }

  prepare(name: string, body: unknown): () => unknown {
    const handler = this.commands.get(name);
    if (!handler) throw new Error('Unknown command: ' + name);
    return handler(body);
  }

  invoke(name: string, body: unknown): unknown {
    return this.prepare(name, body)();
  }
}
