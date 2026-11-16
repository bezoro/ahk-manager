import test from 'node:test';
import assert from 'node:assert/strict';
import {
  array,
  bool,
  decimal,
  integer,
  nullable,
  parseStrictJson,
  requireValid,
  shape,
  shapeFor,
  snapshot as validSnapshot,
  text,
} from '../../src/shared/validation.js';
import { snapshot } from './fixtures.js';

interface Row {
  id: string;
  count: number;
  selected?: boolean | null;
}
const row = shapeFor<Row>()({ id: text(12), count: integer }, { selected: nullable(bool) });

test('typed DTO validation preserves required, optional and own-property boundaries', () => {
  for (const selected of [undefined, false, true, null]) {
    const value =
      selected === undefined ? { id: 'row', count: 1 } : { id: 'row', count: 1, selected };
    assert.equal(row(value), true);
    assert.equal(requireValid(value, row, 'row'), value);
  }
  for (const value of [
    null,
    [],
    { id: 'row' },
    { id: 'row', count: 1, selected: undefined },
    { id: 'row', count: 1, selected: 'true' },
    { id: 'row', count: 1, extra: true },
    Object.assign(Object.create({ id: 'inherited' }), { count: 1 }),
    JSON.parse('{"id":"row","count":1,"__proto__":{}}'),
  ]) {
    assert.equal(row(value), false);
    assert.throws(() => requireValid(value, row, 'row'), /Invalid row/);
  }
});

test('inferred nested validation retains limits, types and unknown-field rejection', () => {
  const page = shape({ rows: array(row, 2) }, { label: nullable(text(6)) });
  const value = {
    rows: [
      { id: 'one', count: 1 },
      { id: 'two', count: 2 },
    ],
    label: null,
  };
  assert.equal(requireValid(value, page, 'page'), value);
  for (const invalid of [
    { rows: [...value.rows, { id: 'three', count: 3 }] },
    { rows: [{ id: 'one', count: 1, forged: true }] },
    { rows: [{ id: 'one', count: '1' }] },
    { rows: value.rows, label: 'too long' },
    { rows: value.rows, unexpected: 0 },
  ])
    assert.equal(page(invalid), false);
});

test('typing changes preserve numeric and duplicate-key transport safety', () => {
  assert.equal(decimal('9223372036854775807'), true);
  for (const value of ['9223372036854775808', '-1', '00', 1, null])
    assert.equal(decimal(value), false);
  for (const value of [NaN, Infinity, 1.5, Number.MAX_SAFE_INTEGER + 1])
    assert.equal(integer(value), false);
  assert.equal(integer(Number.MAX_SAFE_INTEGER), true);
  assert.throws(() => parseStrictJson('{"rows":[],"r\\u006fws":[1]}'), /Duplicate JSON property/);
  assert.throws(() => parseStrictJson('{"rows":[{"id":1,"id":2}]}'), /Duplicate JSON property/);
});

test('complete snapshot schemas still accept actual DTOs and refuse nested protocol drift', () => {
  const value = snapshot();
  assert.equal(validSnapshot(value), true);
  assert.equal(validSnapshot({ ...value, protocolDrift: true }), false);
  assert.equal(
    validSnapshot({ ...value, settings: { ...value.settings, futureSetting: true } }),
    false,
  );
  assert.equal(validSnapshot({ ...value, settings: { ...value.settings, theme: 'Other' } }), false);
  assert.equal(validSnapshot({ ...value, runningCount: '1' }), false);
});
