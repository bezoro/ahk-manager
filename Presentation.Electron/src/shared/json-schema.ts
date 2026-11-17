import type { Validator } from './validation.js';

type Json = string | number | boolean | null | readonly Json[] | { readonly [key: string]: Json };
export type JsonSchema = { readonly [keyword: string]: Json };

// Discovery metadata is derived lazily. Validators remain the input admission boundary.
const descriptions = new WeakMap<Validator, JsonSchema | (() => JsonSchema)>();

export function described<T extends Validator>(
  check: T,
  schema: JsonSchema | (() => JsonSchema),
): T {
  descriptions.set(check, schema);
  return check;
}

export function jsonSchema(check: Validator): JsonSchema {
  const schema = descriptions.get(check);
  if (!schema) throw new Error('A public command validator is missing its JSON Schema.');
  return typeof schema === 'function' ? schema() : schema;
}
