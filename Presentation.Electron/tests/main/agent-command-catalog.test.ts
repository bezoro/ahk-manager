import assert from 'node:assert/strict';
import test from 'node:test';
import { describeAgentCommands } from '../../src/shared/agent-command-catalog.js';
import { agentCommandSchemas } from '../../src/shared/agent-command-schemas.js';
import metadata from '../../src/shared/agent-commands.json';
import { runningSnapshot } from './fixtures.js';

test('the packaged agent catalog matches the authoritative command argument validators', () => {
  assert.deepEqual(
    describeAgentCommands(metadata),
    metadata,
    'Run node scripts/generate-agent-catalog.cjs after changing command schemas.',
  );
});

test('agent argument validators accept catalog data and reject malformed nested input', () => {
  const { revision, settings } = runningSnapshot();
  const save = agentCommandSchemas['settings.save'];
  assert.equal(save({ revision, settings }), true);
  assert.equal(save({ revision: Number(revision), settings }), false);
  assert.equal(save({ revision, settings: { ...settings, window: { dpi: 96 } } }), false);
  assert.equal(save({ revision, settings: { ...settings, extra: true } }), false);

  const add = agentCommandSchemas['scripts.addFiles'];
  assert.equal(add({ paths: ['C:\\Scripts\\demo.ahk', '\\\\server\\share\\demo.ahk'] }), true);
  assert.equal(add({ paths: ['scripts\\demo.ahk'] }), false);
  assert.equal(add({ paths: [] }), false);
});

test('agent discovery advertises only eligible review targets and binds confirmation to their shapes', () => {
  const { inputSchema } = metadata.find((entry) => entry.name === 'commands.preview')!;
  const variants = inputSchema.oneOf!;
  const reviewed = metadata.filter(
    (entry) => entry.surface === 'management' && entry.confirmation !== undefined,
  );
  assert.deepEqual(
    variants.map((variant) => variant.properties.command.const),
    reviewed.map((entry) => entry.name),
  );
  for (const [index, entry] of reviewed.entries()) {
    const previewArguments = variants[index]!.properties.arguments;
    assert.equal(previewArguments.additionalProperties, false);
    assert.deepEqual(entry.inputSchema.required, [
      ...previewArguments.required,
      'confirmationToken',
    ]);
    const { confirmationToken, ...argumentsSchema } = entry.inputSchema.properties;
    assert.equal(confirmationToken?.format, 'uuid');
    assert.deepEqual(argumentsSchema, previewArguments.properties);
  }
});
