import { agentCommandSchemas, type AgentCommandName } from './agent-command-schemas.js';
import { jsonSchema, type JsonSchema } from './json-schema.js';
import { guid } from './validation.js';

interface Entry {
  name: string;
  description: string;
  execution: string;
  surface: string;
  effect: string;
  confirmation?: string;
}

/** The checked-in catalog lets native builds package the actual validators' schemas without Node. */
export function describeAgentCommands<T extends Entry>(catalog: readonly T[]) {
  const names = new Set(catalog.map(({ name }) => name));
  if (
    names.size !== catalog.length ||
    names.size !== Object.keys(agentCommandSchemas).length ||
    catalog.some(
      (entry) =>
        !Object.hasOwn(agentCommandSchemas, entry.name) ||
        !/^[a-z][a-zA-Z0-9]*\.[a-z][a-zA-Z0-9]*$/.test(entry.name) ||
        entry.description.length === 0 ||
        entry.execution !== (entry.name === 'app.start' ? 'client' : 'host') ||
        !['management', 'desktop'].includes(entry.surface) ||
        !['read', 'change', 'destructive'].includes(entry.effect),
    )
  )
    throw new Error('The catalog and agent command validators differ.');

  const inputFor = (name: string) => jsonSchema(agentCommandSchemas[name as AgentCommandName]);
  const reviewed = catalog.filter(
    (entry) => entry.surface === 'management' && entry.confirmation !== undefined,
  );
  return catalog.map((entry) => {
    const schema = inputFor(entry.name);
    let inputSchema: JsonSchema = schema;
    if (entry.name === 'commands.preview') {
      inputSchema = {
        ...schema,
        oneOf: reviewed.map(({ name }) => ({
          properties: { command: { const: name }, arguments: inputFor(name) },
        })),
      };
    } else if (entry.confirmation !== undefined) {
      inputSchema = {
        ...schema,
        properties: {
          ...(schema.properties as Record<string, JsonSchema>),
          confirmationToken: {
            ...jsonSchema(guid),
            description: 'Use the token returned by commands.preview for the same arguments.',
          },
        },
        required: [...(schema.required as readonly string[]), 'confirmationToken'],
      };
    }
    return { ...entry, inputSchema };
  });
}
