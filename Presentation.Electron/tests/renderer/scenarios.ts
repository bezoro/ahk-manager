import type { HostState, ManagerSnapshot, ScriptSnapshot, ThemeState } from '../../src/shared/contracts.js';
import { outcome, reason, text } from '../../src/renderer/resources/pt-BR.js';

declare global { interface Window { fixtureHarness: { setResponse(method: string, response: { value?: unknown; error?: string; pending?: boolean }): Promise<void>; resolveResponse(method: string, response: { value?: unknown; error?: string }): Promise<void>; resetResponses(): Promise<void>; openPreferences(): Promise<void>; setState(state: HostState): Promise<void>; setTheme(theme: ThemeState): Promise<void>; calls(): Promise<Array<{ method: string; args: unknown[] }>>; clearCalls(): Promise<void> } } }
interface AssertionRecord { name: string; passed: boolean; detail?: string }
const tick = (): Promise<void> => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
async function until(predicate: () => boolean, label: string): Promise<void> { const deadline = performance.now() + 3000; do { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 20)); } while (performance.now() < deadline); throw new Error(label); }
function assert(value: unknown, label: string): asserts value { if (!value) throw new Error(label); }
function button(label: string, root: ParentNode = document): HTMLButtonElement { const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(node => node.textContent?.trim() === label || node.getAttribute('aria-label') === label); assert(found, 'Missing button: ' + label); return found; }
function input(label: string): HTMLInputElement { const node = document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`); assert(node, 'Missing input: ' + label); return node; }
function edit(node: HTMLInputElement | HTMLTextAreaElement, value: string): void { const prototype = node instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(node, value); node.dispatchEvent(new Event('input', { bubbles: true })); }
function selectScript(id: string): void { const row = document.getElementById('script-' + id); assert(row, 'Missing script: ' + id); row.click(); }
function script(id: string, patch: Partial<ScriptSnapshot> = {}): ScriptSnapshot { return { id, friendlyName: 'Script ' + id, path: `C:\\FIXTURE ISOLADA\\${id}.ahk`, description: '', collectionId: '33333333-3333-3333-3333-333333333333', runAtLogin: false, runAsAdministrator: false, launchBlocked: false, status: 'Stopped', process: null, hotkeys: [], coverage: 'Partial', issues: [], ...patch }; }
function state(): HostState { const snapshot: ManagerSnapshot = { revision: '9007199254741001', scripts: [script('11111111-1111-1111-1111-111111111111', { friendlyName: 'Editor de teste', hotkeys: [{ chord: '^j', normalizedChord: '^j', origin: 'Manual', context: null, sourcePath: null, line: null, isRemap: false, possibleConflict: false }] }), script('22222222-2222-2222-2222-222222222222', { friendlyName: 'Navegador de teste', collectionId: null })], collections: [{ id: '33333333-3333-3333-3333-333333333333', name: 'Trabalho de teste' }], settings: { interpreterPath: null, editorPath: null, editorArguments: [], launchManagerAtLogin: false, showWindowAtLogin: false, theme: 'System', closeBehavior: 'HideToTray', window: { x: 0, y: 0, width: 1240, height: 820, maximized: false, dpi: 96 } }, activity: [], issues: [{ code: 'PartialAnalysis', detail: 'FIXTURE ISOLADA — sem scripts, perfis ou efeitos reais.', scriptId: null }], readOnly: false, effectsAvailable: true, observedAt: '2026-10-23T00:00:00Z' }; return { connection: 'ready', snapshot, error: null, busy: false }; }
async function untilCall(method: string): Promise<void> { for (let attempt = 0; attempt < 100; attempt++) { if ((await window.fixtureHarness.calls()).some(call => call.method === method)) return; await new Promise(resolve => setTimeout(resolve, 20)); } throw new Error('No fixture call: ' + method); }
const light: ThemeState = { dark: false, highContrast: false, reducedMotion: false, accent: '#627c47', material: 'solid' };

export async function runScenarios(): Promise<AssertionRecord[]> {
  const results: AssertionRecord[] = [];
  async function check(name: string, action: () => Promise<void>): Promise<void> { try { await action(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, detail: String(error) }); } }
  const previousDialog = document.querySelector('[role="dialog"]'); if (previousDialog) button('Cancelar', previousDialog).click(); await window.fixtureHarness.resetResponses(); button('Scripts').click(); const oldSearch = document.querySelector<HTMLInputElement>('input[aria-label="Pesquisar scripts"]'); if (oldSearch) edit(oldSearch, '');
  const host = state(); await window.fixtureHarness.setState(host); await window.fixtureHarness.setTheme(light); await until(() => !!document.getElementById('script-11111111-1111-1111-1111-111111111111'), 'Renderer did not show fixture rows');
  await check('actual snapshot labels, startup heading and accessible list options', async () => { assert(document.querySelector('h1')?.textContent === 'Scripts', 'Page heading'); assert(document.querySelector('.table-head')?.textContent?.includes('Iniciar com Windows'), 'Explicit startup heading'); assert(document.querySelectorAll('[role="option"]').length === 2, 'Real fixture rows'); assert(document.querySelector('.native-title')?.textContent === 'AHK Manager', 'Native caption drag surface'); });
  await check('manual metadata draft, focus and caret survive live theme and snapshot updates', async () => {
    selectScript('11111111-1111-1111-1111-111111111111'); await until(() => !!document.getElementById('draft-name'), 'Inspector missing'); const details = document.querySelector<HTMLDetailsElement>('.metadata-expander')!; details.open = true;
    const name = document.getElementById('draft-name') as HTMLInputElement; const hotkeys = document.getElementById('draft-hotkeys') as HTMLTextAreaElement; edit(name, 'Nome em rascunho'); edit(hotkeys, '^k\n\n'); name.focus(); name.setSelectionRange(2, 6); await tick();
    await window.fixtureHarness.setTheme({ ...light, dark: true }); await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, revision: '9007199254741002' } }); await tick();
    assert(document.getElementById('draft-name') === name, 'Input remounted'); assert(name.value === 'Nome em rascunho', 'Draft lost'); assert(hotkeys.value === '^k\n\n', 'Raw textarea changed'); assert(document.activeElement === name && name.selectionStart === 2 && name.selectionEnd === 6, 'Focus/caret lost'); assert(document.querySelector('.app-root')?.getAttribute('data-dark') === 'true', 'Live dark state not applied');
    await window.fixtureHarness.setTheme(light); await tick(); assert(name.value === 'Nome em rascunho', 'Returning light lost draft');
  });
  await check('same-selection collection refresh removes stale membership and preserves other edits', async () => {
    const changed = { ...host, snapshot: { ...host.snapshot!, revision: '9007199254741003', collections: [{ id: '44444444-4444-4444-4444-444444444444', name: 'Nova coleção' }] } }; await window.fixtureHarness.setState(changed); await tick();
    const select = document.getElementById('draft-collection') as HTMLSelectElement; assert(select.value === '', 'Removed membership remained selected'); assert([...select.options].some(option => option.textContent === 'Nova coleção'), 'New collection not reachable'); assert((document.getElementById('draft-name') as HTMLInputElement).value === 'Nome em rascunho', 'Unrelated draft reset');
  });
  await check('search hides action target immediately and recoverable draft remains after restoring filter', async () => {
    await window.fixtureHarness.clearCalls(); edit(input('Pesquisar scripts'), 'nada encontrado'); await until(() => !document.getElementById('script-11111111-1111-1111-1111-111111111111'), 'Search did not filter'); await tick(); const calls = await window.fixtureHarness.calls(); assert(calls.some(call => call.method === 'updateSelection' && call.args[0] === null), 'Hidden selection not invalidated'); assert(!document.getElementById('draft-name'), 'Hidden row still inspected');
    edit(input('Pesquisar scripts'), ''); await until(() => !!document.getElementById('script-11111111-1111-1111-1111-111111111111'), 'Search did not restore'); selectScript('11111111-1111-1111-1111-111111111111'); await tick(); assert((document.getElementById('draft-name') as HTMLInputElement).value === 'Nome em rascunho', 'Filtered draft lost');
  });
  await check('unknown row remains inspectable with safe file actions and blocked process effects', async () => {
    await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, revision: '9007199254741004', scripts: [{ ...host.snapshot!.scripts[0]!, status: 'Unknown' }, host.snapshot!.scripts[1]!] } }); await tick(); const panel = document.querySelector('.inspector')!;
    assert(button('Iniciar', panel).disabled && button('Parar', panel).disabled && button('Reiniciar', panel).disabled, 'Unknown process controls enabled'); assert(!button('Editar script', panel).disabled && !button('Abrir pasta', panel).disabled, 'Safe unknown file actions unavailable'); assert(!!document.getElementById('draft-name'), 'Unknown details inaccessible');
  });
  await check('owner unavailable disables toolbar and context actions consistently without dispatch', async () => {
    await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, revision: '9007199254741005', effectsAvailable: false } }); await tick(); await window.fixtureHarness.clearCalls(); const row = document.getElementById('script-11111111-1111-1111-1111-111111111111')!; row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 400, clientY: 230 })); await until(() => !!document.querySelector('.script-context'), 'Context menu absent');
    const menu = document.querySelector('.script-context')!; assert(button('Iniciar', menu).disabled, 'Context Start enabled'); assert(button('Iniciar', document.querySelector('.inspector')!).disabled, 'Inspector Start enabled'); assert(!button('Editar script', menu).disabled, 'Safe context Edit disabled'); assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'operate'), 'Merely opening menu dispatched'); document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  });
  await check('keyboard list navigation selects actual stable row identity', async () => {
    await window.fixtureHarness.setState(host); await tick(); const list = document.querySelector<HTMLElement>('[role="listbox"]')!; list.focus(); list.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true })); await tick(); assert(document.getElementById('script-22222222-2222-2222-2222-222222222222')?.getAttribute('aria-selected') === 'true', 'End did not select last row'); assert(list.getAttribute('aria-activedescendant') === 'script-22222222-2222-2222-2222-222222222222', 'Active descendant stale');
  });
  await check('preferences use true system option and preserve draft until explicit save', async () => {
    await window.fixtureHarness.clearCalls(); button('Preferências').click(); await until(() => !!document.getElementById('settings-theme'), 'Preferences missing'); const theme = document.getElementById('settings-theme') as HTMLSelectElement; assert([...theme.options].some(option => option.value === 'System' && option.text === 'Seguir o sistema'), 'Automatic system option absent'); theme.value = 'Dark'; theme.dispatchEvent(new Event('change', { bubbles: true })); await tick(); assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'saveSettings'), 'Draft applied without save'); button('Cancelar', document.querySelector('[role="dialog"]')!).click(); await tick();
  });
  await check('activity retains two-script association and local friendly-name search', async () => {
    await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, revision: '9007199254741006', activity: host.snapshot!.scripts.map(item => ({ id: item.id, timestamp: '2026-10-23T00:00:00Z', scriptId: item.id, operation: 'Start', outcome: 'Confirmed', code: 'None', detail: 'FIXTURE ISOLADA' })) } }); button('Atividade').click(); await until(() => document.querySelectorAll('.activity-list article').length === 2, 'Activity fixture records missing'); edit(input('Pesquisar'), 'Navegador de teste'); await tick(); assert(document.querySelectorAll('.activity-list article').length === 1, 'Friendly-name activity filter failed'); assert(document.querySelector('.activity-list article')?.textContent?.includes('Navegador de teste'), 'Script association missing'); button('Scripts').click(); await tick();
  });
  await check('loaded-library help explains first-use migration without dispatching an unavailable operation', async () => {
    await window.fixtureHarness.clearCalls(); button('Ajuda').click(); await until(() => !!document.querySelector('[role="dialog"]'), 'Help missing'); const dialog = document.querySelector('[role="dialog"]')!;
    assert(button('Migrar perfil WinUI…', dialog).disabled, 'Loaded catalog advertised available migration'); assert(dialog.textContent?.includes('somente na primeira abertura, antes de carregar o catálogo'), 'First-use restriction not explained'); assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'migrateProfile'), 'Help dispatched migration'); button('Fechar', dialog).click(); await tick();
  });
  await check('footer reports actual problem count, connection and pending request without claiming all-clear', async () => {
    await window.fixtureHarness.setState({ ...host, busy: true, snapshot: { ...host.snapshot!, problemCount: 3 } }); await tick(); const footer = document.querySelector<HTMLElement>('.status-footer')!;
    assert(footer.textContent?.includes('3 problemas informados') && footer.textContent.includes('Solicitação em andamento'), 'Real problem/busy summary missing'); assert(footer.getAttribute('aria-live') === 'polite', 'Footer status inaccessible');
    await window.fixtureHarness.setState({ ...host, connection: 'failed', snapshot: { ...host.snapshot!, problemCount: 3 } }); await tick(); assert(footer.textContent?.includes('Biblioteca indisponível'), 'Connection failure hidden'); assert(footer.scrollWidth <= footer.clientWidth, 'Footer horizontal overflow'); await window.fixtureHarness.setState(host); await tick();
  });
  await check('native-theme accessibility settings and layout stay bounded in current viewport', async () => {
    await window.fixtureHarness.setTheme({ ...light, highContrast: true, reducedMotion: true }); await tick(); const root = document.querySelector('.app-root')!; assert(root.getAttribute('data-contrast') === 'true' && root.getAttribute('data-reduced-motion') === 'true', 'Accessibility settings not applied'); assert(document.documentElement.scrollWidth <= innerWidth, 'Document horizontal overflow'); assert(document.querySelector('.main-column')!.scrollWidth <= document.querySelector('.main-column')!.clientWidth + 1, 'Main column horizontal overflow'); await window.fixtureHarness.setTheme(light);
  });
  const activeDialog = (): HTMLElement => { const node = document.querySelector<HTMLElement>('[role="dialog"]'); assert(node, 'Active dialog missing'); assert(!node.closest('[aria-hidden="true"]'), 'Dialog hidden from accessibility tree'); return node; };
  const failure = { outcome: 'Failed', code: 'PersistenceFailed', detail: 'FIXTURE diagnostic detail', correlationId: null };
  const open = async (kind: string): Promise<void> => {
    if (kind === 'preferences') button(text.preferences).click();
    else if (kind === 'collection') button(text.createCollection).click();
    else { await window.fixtureHarness.setResponse('previewImport', { value: { token: 'fixture-import', preview: { sourcePath: 'C:\\FIXTURE\\legacy.json', sourceHash: 'fixture', scriptCount: 2, canImport: true, issues: [] } } }); button(text.help).click(); await tick(); button(text.importLegacy, activeDialog()).click(); }
    await until(() => !!document.querySelector('[role="dialog"]') && (kind !== 'import' || !!document.querySelector('[role="dialog"] .file-value')), 'Dialog did not open'); await tick();
  };
  for (const kind of ['preferences', 'collection', 'import']) {
    await check(kind + ' keeps failures, rejection and nonfinal outcomes in its dialog', async () => {
      const method = kind === 'preferences' ? 'saveSettings' : kind === 'collection' ? 'saveCollection' : 'commitImport';
      await open(kind); const dialog = activeDialog();
      const draft = kind === 'preferences' ? dialog.querySelector<HTMLTextAreaElement>('#settings-editor-arguments') : dialog.querySelector<HTMLInputElement>('#collection-name');
      if (draft) { edit(draft, 'FIXTURE edited draft'); await tick(); }
      const source = dialog.querySelector('.file-value')?.textContent;
      for (const value of ['Failed', 'Partial', 'Unknown', 'Accepted', 'Pending'] as const) {
        await window.fixtureHarness.setResponse(method, { value: { ...failure, outcome: value } }); button(kind === 'import' ? text.import : text.save, dialog).click();
        await until(() => dialog.querySelector('.notice strong')?.textContent === outcome(value), value + ' feedback missing inside dialog');
        assert(activeDialog() === dialog, 'Dialog remounted'); assert(dialog.querySelector('[role="alert"]')?.textContent?.includes(reason(failure.code)), 'Semantic reason missing');
        assert(dialog.querySelector('.notice pre')?.textContent === failure.detail, 'Technical detail missing');
        if (draft) assert(draft.value === 'FIXTURE edited draft', 'Edited draft lost'); else assert(dialog.querySelector('.file-value')?.textContent === source, 'Import preview lost');
      }
      await window.fixtureHarness.setResponse(method, { error: 'FIXTURE rejected request' }); button(kind === 'import' ? text.import : text.save, dialog).click(); await until(() => dialog.querySelector('.notice pre')?.textContent?.includes('FIXTURE rejected request') === true, 'Thrown error not in dialog');
      await window.fixtureHarness.setResponse(method, { value: { ...failure, outcome: 'CancelledBeforeDispatch' } }); button(kind === 'import' ? text.import : text.save, dialog).click(); await until(() => !dialog.querySelector('.notice'), 'Cancellation appeared as failure'); assert(activeDialog() === dialog, 'Cancellation closed draft');
      await window.fixtureHarness.setResponse(method, { value: { ...failure, outcome: 'Confirmed', code: 'None' } }); button(kind === 'import' ? text.import : text.save, dialog).click(); await until(() => !document.querySelector('[role="dialog"]'), 'Confirmed did not close');
      await open(kind); assert(!activeDialog().querySelector('.notice'), 'Stale feedback after reopen'); button(text.cancel, activeDialog()).click(); await tick();
    });
  }
  await check('picker, startup feedback and late completions respect preferences sessions', async () => {
    await open('preferences'); let dialog = activeDialog(); const draft = dialog.querySelector<HTMLTextAreaElement>('#settings-editor-arguments')!; edit(draft, 'FIXTURE preserved'); await tick();
    await window.fixtureHarness.setResponse('pickInterpreter', { error: 'FIXTURE picker failure' }); button(text.choose, dialog).click(); await until(() => dialog.querySelector('.notice pre')?.textContent?.includes('FIXTURE picker failure') === true, 'Picker error hidden'); assert(draft.value === 'FIXTURE preserved', 'Picker reset draft');
    await window.fixtureHarness.setResponse('pickInterpreter', { value: null }); button(text.choose, dialog).click(); await until(() => !dialog.querySelector('.notice') && !button(text.choose, dialog).matches(':disabled'), 'Null picker reported failure');
    await window.fixtureHarness.setResponse('transferStartup', { value: { ...failure, outcome: 'Partial' } }); button(text.transferStartup, dialog).click(); await until(() => dialog.querySelector('.notice strong')?.textContent === outcome('Partial'), 'Transfer result hidden');
    for (const response of [{ value: 'C:\\FIXTURE\\late.exe' }, { error: 'FIXTURE late picker rejection' }]) {
      await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('pickInterpreter', { pending: true }); button(text.choose, dialog).click(); await untilCall('pickInterpreter');
      button(text.cancel, dialog).click(); await tick(); await window.fixtureHarness.openPreferences(); await tick(); dialog = activeDialog();
      await window.fixtureHarness.resolveResponse('pickInterpreter', response); await tick(); assert(!dialog.querySelector('.notice'), 'Late completion leaked to reopened dialog'); assert(dialog.querySelector<HTMLInputElement>('input[aria-label="' + text.interpreter + '"]')?.value === '', 'Late picker populated reopened draft');
    }
    button(text.cancel, dialog).click(); await tick();
  });
  await check('pending dialog save blocks duplicate dispatch and waits for completion', async () => {
    await open('preferences'); const dialog = activeDialog(); await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveSettings', { pending: true }); button(text.save, dialog).click(); await untilCall('saveSettings'); await tick();
    assert(button(text.save, dialog).disabled && button(text.cancel, dialog).disabled, 'Pending controls not guarded'); button(text.save, dialog).click(); assert((await window.fixtureHarness.calls()).filter(call => call.method === 'saveSettings').length === 1, 'Duplicate dispatch'); assert(!dialog.querySelector('.notice.success'), 'Pending claimed success');
    await window.fixtureHarness.resolveResponse('saveSettings', { value: failure }); await until(() => !!dialog.querySelector('.notice'), 'Pending result absent'); button(text.cancel, dialog).click(); await tick();
  });
  await check('native preferences replaces another modal and ignores its late completion', async () => {
    for (const dismiss of ['cancel', 'escape']) {
      button(text.help).click(); await tick(); const help = activeDialog(); assert(help.textContent?.includes(text.helpDescription), 'Help did not open');
      await window.fixtureHarness.openPreferences(); await until(() => !!document.getElementById('settings-theme'), 'Preferences did not replace Help');
      const preferences = activeDialog(); assert(document.querySelectorAll('[role="dialog"]').length === 1 && !help.isConnected, 'Help remained behind Preferences');
      if (dismiss === 'cancel') button(text.cancel, preferences).click();
      else preferences.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await until(() => !document.querySelector('[role="dialog"]'), 'Help stranded after Preferences closed'); await tick();
      assert(!document.activeElement?.closest('[aria-hidden="true"],[inert]'), 'Focus remained in hidden modal content');
      const trigger = button(text.preferences); trigger.focus(); assert(document.activeElement === trigger && !trigger.closest('[aria-hidden="true"],[inert]'), 'Navigation focus unavailable');
    }
    for (const response of [{ value: failure }, { value: { ...failure, outcome: 'Confirmed' } }, { error: 'FIXTURE late collection rejection' }]) {
      await open('collection'); const collection = activeDialog(); edit(collection.querySelector<HTMLInputElement>('#collection-name')!, 'FIXTURE pending collection'); await tick();
      await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveCollection', { pending: true }); button(text.save, collection).click(); await untilCall('saveCollection');
      await window.fixtureHarness.openPreferences(); await until(() => !!document.getElementById('settings-theme'), 'Native preferences did not replace modal');
      const preferences = activeDialog(); assert(document.querySelectorAll('[role="dialog"]').length === 1 && !document.getElementById('collection-name'), 'Two modal surfaces active');
      await window.fixtureHarness.resolveResponse('saveCollection', response); await until(() => !button(text.cancel, preferences).disabled, 'Pending completion did not release busy state'); await tick();
      assert(activeDialog() === preferences && !preferences.querySelector('.notice'), 'Unrelated completion changed preferences'); button(text.cancel, preferences).click(); await tick();
    }
  });
  await check('list retains usable rows, scrolling, keyboard selection and separate inspector', async () => {
    await window.fixtureHarness.setState(host); await tick(); selectScript(host.snapshot!.scripts[0]!.id); await tick();
    const metadata = document.querySelector<HTMLDetailsElement>('.metadata-expander')!; metadata.open = true; edit(document.getElementById('draft-name') as HTMLInputElement, 'FIXTURE layout draft'); await tick();
    await window.fixtureHarness.setResponse('saveMetadata', { value: { ...failure, detail: 'FIXTURE expanded diagnostic\n'.repeat(35) } }); button(text.save, document.querySelector('.inspector')!).click(); await until(() => !!document.querySelector('.main-column>.notice'), 'Layout notice missing');
    const workspace = document.querySelector<HTMLElement>('.workspace')!, viewport = document.querySelector<HTMLElement>('.script-viewport')!, inspector = document.querySelector<HTMLElement>('.inspector')!;
    const verify = (): void => { const list = viewport.getBoundingClientRect(), filters = document.querySelector('.script-filters')!.getBoundingClientRect(), details = inspector.getBoundingClientRect(); assert(viewport.clientHeight >= 106, 'No usable row area'); assert(list.top >= filters.bottom - 1, 'Filters overlap rows'); if (innerWidth <= 900 && !inspector.hidden) { assert(details.top >= list.bottom - 1, 'Inspector overlaps list'); assert(workspace.scrollHeight > workspace.clientHeight || details.bottom <= workspace.getBoundingClientRect().bottom + 1, 'Inspector unreachable'); } assert(document.querySelector('.script-row')!.getBoundingClientRect().height >= 60, 'Row collapsed'); };
    verify(); const noticeDetails = document.querySelector<HTMLDetailsElement>('.main-column>.notice details')!; noticeDetails.open = true; const analysis = document.querySelector<HTMLDetailsElement>('.library-notice details')!; analysis.open = true; await tick(); verify();
    viewport.scrollIntoView({ block: 'center' }); viewport.focus(); viewport.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true })); await tick(); assert(viewport.getAttribute('aria-activedescendant') === 'script-' + host.snapshot!.scripts[1]!.id, 'Keyboard selection lost'); assert(viewport.getBoundingClientRect().top < workspace.getBoundingClientRect().bottom, 'List cannot scroll into reach');
    if (innerWidth <= 900) { button(text.hideDetails, inspector).click(); await tick(); assert(inspector.hidden, 'Details did not close'); verify(); button(text.details).click(); await tick(); assert(!inspector.hidden, 'Details did not reopen'); }
    noticeDetails.open = false; analysis.open = false; metadata.open = false; workspace.scrollTop = 0; await tick(); verify(); await window.fixtureHarness.resetResponses();
  });
  await check('root header is escaped multiline primary text; refresh/removal preserve editable manual fallback draft', async () => {
    button(text.scripts).click(); const search = document.querySelector<HTMLInputElement>('input[aria-label="Pesquisar scripts"]'); if (search) edit(search, '');
    const header = '<script>literal sem executar</script>\nSegunda linha — ' + 'palavra'.repeat(110);
    const withHeader: HostState = { ...host, snapshot: { ...host.snapshot!, scripts: [{ ...host.snapshot!.scripts[0]!, description: 'Manual persistida', headerDescription: header }, host.snapshot!.scripts[1]!] } };
    await window.fixtureHarness.setState(withHeader); selectScript(withHeader.snapshot!.scripts[0]!.id); await until(() => !!document.getElementById('draft-description'), 'Description editor missing');
    const panel = document.querySelector<HTMLElement>('.inspector')!; const discard = button(text.discard, panel); if (!discard.disabled) { discard.click(); await tick(); }
    const metadata = panel.querySelector<HTMLDetailsElement>('.metadata-expander')!; metadata.open = true;
    const manual = document.getElementById('draft-description') as HTMLTextAreaElement;
    assert(String(manual.value) === 'Manual persistida', 'Header overwrote manual metadata');
    assert(panel.textContent?.includes(text.headerDescription) && panel.textContent.includes(text.headerDescriptionHint), 'Header source/edit guidance missing');
    edit(manual, 'Manual em edição'); await tick(); await window.fixtureHarness.clearCalls();
    for (const dark of [false, true]) {
      await window.fixtureHarness.setTheme({ ...light, dark }); await tick();
      const paragraph = panel.querySelector<HTMLElement>('.description')!;
      assert(paragraph.textContent === header && !paragraph.querySelector('script,img'), 'Header HTML interpreted instead of escaped');
      assert(getComputedStyle(paragraph).whiteSpace === 'pre-wrap' && getComputedStyle(paragraph).overflowWrap === 'anywhere', 'Header multiline/wrapping lost');
      assert(paragraph.scrollWidth <= paragraph.clientWidth + 1 && paragraph.scrollHeight <= paragraph.clientHeight + 1, 'Header text clipped');
    }
    await window.fixtureHarness.setState({ ...withHeader, snapshot: { ...withHeader.snapshot!, revision: '9007199254741800', scripts: [{ ...withHeader.snapshot!.scripts[0]!, headerDescription: 'Cabeçalho atualizado' }, withHeader.snapshot!.scripts[1]!] } }); await tick();
    assert(manual.value === 'Manual em edição' && panel.querySelector('.description')?.textContent === 'Cabeçalho atualizado', 'External header refresh replaced dirty manual draft');
    await window.fixtureHarness.setState({ ...withHeader, snapshot: { ...withHeader.snapshot!, scripts: [{ ...withHeader.snapshot!.scripts[0]!, headerDescription: null }, withHeader.snapshot!.scripts[1]!] } }); await tick();
    assert(panel.querySelector('.description')?.textContent === 'Manual persistida' && manual.value === 'Manual em edição', 'Header removal did not restore snapshot manual fallback independently');
    assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'saveMetadata'), 'Derived refresh persisted without explicit save');
    await window.fixtureHarness.setState(withHeader); await tick(); metadata.open = false; panel.scrollTop = 0;
  });
  return results;
}
