import { test } from 'node:test';
import assert from 'node:assert/strict';
import type { ActivitySnapshot, HostState, ManagerSnapshot, ScriptSnapshot } from '../../src/shared/contracts.js';
import { actionReason, activityAssociation, brandPalette, changeDraft, changeManualDraft, defaultQuery, effectiveDescription, filterActivity, metadataOf, newDraft, nextQuery, reconcileDraft, rowWindow, visibleScripts } from '../../src/renderer/model.js';

test('header precedence and effective description search preserve only manual metadata and dirty drafts', () => {
  const row = script('header', { description: 'manual fallback', headerDescription: 'Cabeçalho\n<script>literal</script>' });
  assert.equal(effectiveDescription(row), row.headerDescription);
  assert.equal(metadataOf(row).description, 'manual fallback');
  const draft = newDraft(row); assert.equal(draft.dirty, false);
  assert.equal(reconcileDraft(draft, { ...row, headerDescription: 'external refresh' }, new Set()), draft);
  const dirty = changeDraft(draft, { description: 'unsaved manual' });
  assert.equal(reconcileDraft(dirty, { ...row, headerDescription: null }, new Set()), dirty);
  assert.equal(dirty.value.description, 'unsaved manual');
  assert.equal(visibleScripts(snapshot([row]), { ...defaultQuery(), search: 'literal' }).length, 1);
  assert.equal(visibleScripts(snapshot([row]), { ...defaultQuery(), search: 'manual fallback' }).length, 0);
  assert.equal(effectiveDescription({ ...row, headerDescription: null }), 'manual fallback');
  assert.equal(effectiveDescription({ ...row, headerDescription: '' }), 'manual fallback');
  assert.equal(visibleScripts(snapshot([{ ...row, headerDescription: null }]), { ...defaultQuery(), search: 'manual fallback' }).length, 1);
});

// Isolated model fixtures only. Production renders exclusively host snapshots.
function script(id: string, patch: Partial<ScriptSnapshot> = {}): ScriptSnapshot { return { id, friendlyName: id, path: `C:\\fixture\\${id}.ahk`, description: '', collectionId: null, runAtLogin: null, runAsAdministrator: false, launchBlocked: false, status: 'Stopped', process: null, hotkeys: [], coverage: 'Partial', issues: [], ...patch }; }
function snapshot(scripts: ScriptSnapshot[]): ManagerSnapshot { return { revision: '9007199254740999', scripts, collections: [{ id: 'work', name: 'Trabalho' }], settings: { interpreterPath: null, editorPath: null, editorArguments: [], launchManagerAtLogin: false, showWindowAtLogin: false, theme: 'System', closeBehavior: 'HideToTray', window: { x: 0, y: 0, width: 1240, height: 820, maximized: false, dpi: 96 } }, activity: [], issues: [], readOnly: false, effectsAvailable: true, observedAt: null }; }
function host(scripts: ScriptSnapshot[]): HostState { return { snapshot: snapshot(scripts), busy: false, error: null, connection: 'ready' }; }
const process: NonNullable<ScriptSnapshot['process']> = { identity: { pid: 42, creationFileTime: '133700000000000001', executablePath: 'C:\\fixture\\AutoHotkey64.exe', scriptPath: 'C:\\fixture\\one.ahk', userSid: 'fixture', sessionId: 1, scriptHash: 'hash', interpreterHash: 'hash' }, origin: 'Manager', interpreterVersion: '2.0.30', startedAt: '2026-01-01T00:00:00Z', paused: null, suspended: null, windowVerified: true };

test('query revisions retain decimal precision and invalidate on reversible filter changes', () => {
  const initial = { ...defaultQuery(), version: '9007199254740999' }; const search = nextQuery(initial, { search: 'ação' });
  assert.equal(search.version, '9007199254741000'); assert.equal(initial.search, ''); assert.equal(nextQuery(search, { search: '' }).version, '9007199254741001');
});
test('running filter reconciles stopped and unknown observations while unknown stays inspectable in All', () => {
  const running = script('one', { status: 'Running', process }); const query = { ...defaultQuery(), status: 'Running' as const };
  assert.deepEqual(visibleScripts(snapshot([running]), query).map(item => item.id), ['one']);
  for (const status of ['Stopped', 'Unknown'] as const) assert.equal(visibleScripts(snapshot([{ ...running, status }]), query).length, 0);
  assert.equal(visibleScripts(snapshot([{ ...running, status: 'Unknown' }]), defaultQuery())[0]?.id, 'one');
});
test('search includes complete path and collection/sort use current actual records', () => {
  const state = snapshot([script('10', { friendlyName: 'Ação 10', collectionId: 'work' }), script('2', { friendlyName: 'Ação 2' })]);
  assert.deepEqual(visibleScripts(state, defaultQuery()).map(item => item.id), ['2', '10']);
  assert.equal(visibleScripts(state, { ...defaultQuery(), search: 'fixture\\10.ahk' })[0]?.id, '10');
  assert.deepEqual(visibleScripts(state, { ...defaultQuery(), collectionId: 'work' }).map(item => item.id), ['10']);
});
test('dirty metadata and raw manual textarea survive refresh, collection rename and unrelated server metadata', () => {
  const original = script('one', { collectionId: 'work', description: 'saved' }); let draft = changeDraft(newDraft(original), { friendlyName: 'Meu rascunho', description: 'texto local' });
  draft = changeManualDraft(draft, '^j\n\n'); const refreshed = { ...original, friendlyName: 'Servidor', description: 'mudou' };
  assert.equal(reconcileDraft(draft, refreshed, new Set(['work'])), draft); assert.equal(draft.manualText, '^j\n\n'); assert.deepEqual(draft.value.manualHotkeys, ['^j']);
  const removed = reconcileDraft(draft, refreshed, new Set()); assert.equal(removed.value.collectionId, null); assert.equal(removed.value.friendlyName, 'Meu rascunho'); assert.equal(removed.manualText, '^j\n\n'); assert.equal(removed.dirty, true);
});
test('clean drafts adopt real metadata changes but preserve cosmetic textarea/caret identity when unchanged', () => {
  const original = script('one', { hotkeys: [{ chord: '^j', normalizedChord: '^j', origin: 'Manual', context: null, sourcePath: null, line: null, isRemap: false, possibleConflict: false }] });
  const clean = changeManualDraft(newDraft(original), '^j\n'); assert.equal(clean.dirty, false); assert.equal(reconcileDraft(clean, original, new Set()), clean);
  const changed = reconcileDraft(clean, { ...original, description: 'nova descrição' }, new Set()); assert.equal(changed.value.description, 'nova descrição'); assert.notEqual(changed, clean);
  assert.deepEqual(metadataOf(original).manualHotkeys, ['^j']);
});
test('unknown and owner-unavailable rows permit inspection/edit/folder/recovery but never unsafe effects', () => {
  const unknown = script('one', { status: 'Unknown' }); const state = host([unknown]);
  for (const action of ['Edit', 'OpenFolder', 'Recover'] as const) assert.equal(actionReason(state, unknown, action), null);
  for (const action of ['Start', 'Stop', 'Restart', 'TogglePause', 'ToggleSuspend'] as const) assert.notEqual(actionReason(state, unknown, action), null);
  const stopped = script('two'); state.snapshot!.effectsAvailable = false; assert.equal(actionReason(state, stopped, 'Start'), 'EffectOwnerUnavailable'); assert.equal(actionReason(state, stopped, 'Edit'), null);
  state.snapshot!.readOnly = true; assert.equal(actionReason(state, stopped, 'Start'), 'ReadOnlySettings'); assert.equal(actionReason(state, null, 'Edit'), 'StaleSelection');
});
test('toolbar and contextual action eligibility use identical policy including admin restart and unknown suspend', () => {
  const running = script('one', { status: 'Running', process, runAsAdministrator: true }); const state = host([running]);
  assert.equal(actionReason(state, running, 'Start'), 'AlreadyRunning'); assert.equal(actionReason(state, running, 'Restart'), 'ManualElevationRequired'); assert.equal(actionReason(state, running, 'ToggleSuspend'), 'StateNotObservable');
  assert.equal(actionReason(state, running, 'TogglePause'), null); assert.equal(actionReason(state, running, 'Stop'), null);
  assert.equal(actionReason(state, { ...running, process: { ...process, windowVerified: false } }, 'Stop'), 'WindowUnverified');
  assert.equal(actionReason(state, script('blocked', { launchBlocked: true }), 'Start'), 'LegacyGuard');
});
test('activity associates two scripts, searches friendly names/paths and retains removed identity', () => {
  const scripts = [script('one', { friendlyName: 'Editor' }), script('two', { friendlyName: 'Navegador' })];
  const events: ActivitySnapshot[] = scripts.map(item => ({ id: item.id, timestamp: '2026-01-01T00:00:00Z', scriptId: item.id, operation: 'Start', outcome: 'Confirmed', code: 'None', detail: null }));
  assert.equal(filterActivity(events, scripts, 'navegador', null, '')[0]?.scriptId, 'two'); assert.equal(filterActivity(events, scripts, 'one.ahk', null, '').length, 1);
  assert.equal(filterActivity(events, scripts, '', 'one', 'Unknown').length, 0); assert.deepEqual(activityAssociation(events[0]!, []), { name: 'one', path: '' });
});
test('virtual window bounds DOM rows even with huge library and clamps start/end at limits', () => {
  const middle = rowWindow(100000, 340000, 720); assert.ok(middle.end - middle.start < 25); assert.ok(middle.start > 0);
  assert.deepEqual(rowWindow(0, 0, 720), { start: 0, end: 0 }); assert.equal(rowWindow(3, -20, 100).start, 0); assert.equal(rowWindow(3, 100000, 100).end, 3);
});
test('Fluent brand derives actual accent without a forced blue and accepts native alpha suffix', () => {
  assert.equal(brandPalette('#ad4526')[80], '#ad4526'); assert.deepEqual(brandPalette('#ad4526ff'), brandPalette('#ad4526')); assert.notDeepEqual(brandPalette('#ad4526'), brandPalette('#287b55')); assert.equal(brandPalette('invalid')[80], '#737373');
});
