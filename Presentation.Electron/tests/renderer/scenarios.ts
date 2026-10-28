import type { AdministrationUiGuard, HostState, ManagerSnapshot, ScriptSnapshot, ThemeState } from '../../src/shared/contracts.js';
import { operation, outcome, reason, text } from '../../src/renderer/resources/pt-BR.js';

declare global { interface Window { fixtureHarness: { readGuard(): AdministrationUiGuard | null; setResponse(method: string, response: { value?: unknown; error?: string; pending?: boolean }): Promise<void>; resolveResponse(method: string, response: { value?: unknown; error?: string }): Promise<void>; resetResponses(): Promise<void>; openPreferences(): Promise<void>; setState(state: HostState): Promise<void>; setTheme(theme: ThemeState): Promise<void>; calls(): Promise<Array<{ method: string; args: unknown[] }>>; clearCalls(): Promise<void> } } }
interface AssertionRecord { name: string; passed: boolean; detail?: string }
const tick = (): Promise<void> => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
async function until(predicate: () => boolean, label: string): Promise<void> { const deadline = performance.now() + 3000; do { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 20)); } while (performance.now() < deadline); throw new Error(label); }
function assert(value: unknown, label: string): asserts value { if (!value) throw new Error(label); }
function button(label: string, root: ParentNode = document): HTMLButtonElement { const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(node => node.textContent?.trim() === label || node.getAttribute('aria-label') === label); assert(found, 'Missing button: ' + label); return found; }
function input(label: string): HTMLInputElement { const node = document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`); assert(node, 'Missing input: ' + label); return node; }
function edit(node: HTMLInputElement | HTMLTextAreaElement, value: string): void { const prototype = node instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(node, value); node.dispatchEvent(new Event('input', { bubbles: true })); }
function selectScript(id: string): void { const row = document.getElementById('script-' + id); const button = row?.querySelector<HTMLButtonElement>('.row-select'); assert(button, 'Missing selection button: ' + id); button.click(); }
function script(id: string, patch: Partial<ScriptSnapshot> = {}): ScriptSnapshot { return { id, friendlyName: 'Script ' + id, path: `C:\\FIXTURE ISOLADA\\${id}.ahk`, description: '', collectionId: '33333333-3333-3333-3333-333333333333', runAtLogin: false, runAsAdministrator: false, launchBlocked: false, status: 'Stopped', process: null, hotkeys: [], coverage: 'Partial', issues: [], ...patch }; }
function state(): HostState { const snapshot: ManagerSnapshot = { revision: '9007199254741001', scripts: [script('11111111-1111-1111-1111-111111111111', { friendlyName: 'Editor de teste', hotkeys: [{ chord: '^j', normalizedChord: '^j', origin: 'Manual', context: null, sourcePath: null, line: null, isRemap: false, possibleConflict: false }] }), script('22222222-2222-2222-2222-222222222222', { friendlyName: 'Navegador de teste', collectionId: null })], collections: [{ id: '33333333-3333-3333-3333-333333333333', name: 'Trabalho de teste' }], settings: { interpreterPath: null, editorPath: null, editorArguments: [], launchManagerAtLogin: false, showWindowAtLogin: false,startInTray:false, theme: 'System', closeBehavior: 'HideToTray', window: { x: 0, y: 0, width: 1240, height: 820, maximized: false, dpi: 96 } }, activity: [], issues: [{ code: 'PartialAnalysis', detail: 'FIXTURE ISOLADA — sem scripts, perfis ou efeitos reais.', scriptId: null }], readOnly: false, effectsAvailable: true, observedAt: '2026-10-23T00:00:00Z' }; return { connection: 'ready', snapshot, error: null, busy: false, authorityMode: 'Normal', administrationPending: false }; }
async function untilCall(method: string): Promise<void> { for (let attempt = 0; attempt < 100; attempt++) { if ((await window.fixtureHarness.calls()).some(call => call.method === method)) return; await new Promise(resolve => setTimeout(resolve, 20)); } throw new Error('No fixture call: ' + method); }
const light: ThemeState = { dark: false, highContrast: false, reducedMotion: false, accent: '#627c47', material: 'solid' };

export async function runScenarios(): Promise<AssertionRecord[]> {
  const results: AssertionRecord[] = [];
  async function check(name: string, action: () => Promise<void>): Promise<void> { try { await action(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, detail: String(error) }); } }
  const previousDialog = document.querySelector('[role="dialog"]'); if (previousDialog) button('Cancelar', previousDialog).click(); await window.fixtureHarness.resetResponses(); button('Scripts').click(); const oldSearch = document.querySelector<HTMLInputElement>('input[aria-label="Pesquisar scripts"]'); if (oldSearch) edit(oldSearch, '');
  const host = state(); await window.fixtureHarness.setState(host); await window.fixtureHarness.setTheme(light); await until(() => !!document.getElementById('script-11111111-1111-1111-1111-111111111111'), 'Renderer did not show fixture rows');
  await check('actual snapshot labels, startup heading and accessible list options', async () => { assert(document.querySelector('h1')?.textContent === 'Scripts', 'Page heading'); assert(document.querySelector('.table-head')?.textContent?.includes('Iniciar com Windows'), 'Explicit startup heading'); assert(document.querySelectorAll('tbody tr.script-row').length === 2, 'Real fixture rows'); assert(document.querySelector('.native-title>span')?.textContent === 'AHK Manager', 'Native caption drag surface'); });
  await check('readable hotkeys agree across list Inspector page and raw/readable search with accessible details',async()=>{
    const keys=['!^#+u','~LButton Up','F13'].map((chord,index)=>({chord,normalizedChord:chord,origin:'Parsed' as const,context:'fixture context',sourcePath:'C:\\FIXTURE ISOLADA\\generic.ahk',line:index+1,isRemap:false,possibleConflict:index===0,declaredGesture:index===1?'DoubleClick' as const:null}));
    const current={...host,snapshot:{...host.snapshot!,scripts:[{...host.snapshot!.scripts[0]!,hotkeys:keys},host.snapshot!.scripts[1]!]}};
    await window.fixtureHarness.setState(current);await tick();selectScript(current.snapshot.scripts[0]!.id);await tick();
    const label='Ctrl + Alt + Shift + Win + U';assert(document.querySelector('.row-hotkey kbd')?.textContent===label,'List label still raw');
    assert([...document.querySelectorAll('.inspector kbd')].some(node=>node.textContent===label),'Inspector label differs');assert([...document.querySelectorAll('.inspector kbd')].some(node=>node.textContent==='F13'),'F13 changed');
    const key=document.querySelector<HTMLElement>('.row-hotkey kbd')!;key.focus();assert(document.activeElement===key&&!!key.getAttribute('aria-label'),'Readable instruction not keyboard reachable');
    const details=document.querySelector<HTMLDetailsElement>('.inspector .hotkey-explanation')!;details.open=true;assert(details.textContent?.includes('!^#+u'),'Raw syntax lost');assert(document.querySelector('.inspector .conflict-label'),'Conflict badge lost');
    assert(document.querySelector('.script-row')!.getBoundingClientRect().height<=70,'Readable label broke 68px virtualization');
    button(text.hotkeys).click();await tick();assert(document.querySelector('.data-table kbd')?.textContent===label,'Hotkeys page label differs');
    const search=input(text.searchOther);edit(search,'Ctrl + Alt');await tick();assert(document.querySelectorAll('.data-table tbody tr').length===1,'Readable search failed');edit(search,'!^#+u');await tick();assert(document.querySelectorAll('.data-table tbody tr').length===1,'Raw search lost');
    edit(search,'');button(text.scripts).click();await window.fixtureHarness.setState(host);await tick();
  });
  await check('missing file offers direct recovery/removal and dirty draft disables all catalog/file entry points',async()=>{
    const row={...host.snapshot!.scripts[0]!,status:'Missing' as const,issues:[{code:'MissingFile',detail:null,scriptId:null}]};const current={...host,snapshot:{...host.snapshot!,scripts:[row,host.snapshot!.scripts[1]!]}};
    await window.fixtureHarness.setState(current);await tick();selectScript(row.id);await tick();
    const notice=document.querySelector('.missing-file-notice')!;assert(notice.textContent?.includes(row.path),'Old path absent beside recovery');assert(button(text.locateScript,notice)&&button(text.remove,notice),'Missing direct actions');
    await window.fixtureHarness.clearCalls();button(text.locateScript,notice).click();await untilCall('locateScript');assert((await window.fixtureHarness.calls()).filter(call=>call.method==='locateScript').length===1,'Locate dispatch missing');await tick();
    const name=document.getElementById('draft-name') as HTMLInputElement;document.querySelector<HTMLDetailsElement>('.metadata-expander')!.open=true;edit(name,'Dirty file action');await tick();
    assert(button(text.locateScript,notice).disabled&&button(text.remove,notice).disabled&&notice.textContent?.includes(text.fileDraftGuard),'Dirty actions or explanation missing');
    await window.fixtureHarness.clearCalls();document.querySelector('[role="grid"]')!.dispatchEvent(new KeyboardEvent('keydown',{key:'Delete',bubbles:true}));await tick();assert(!(await window.fixtureHarness.calls()).some(call=>call.method==='removeScript'),'Delete bypassed dirty guard');
    button(text.discard).click();await tick();await window.fixtureHarness.setState(host);await tick();assert(button(text.deleteFile,document.querySelector('.inspector')!),'Existing file delete action missing');
  });
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
    await window.fixtureHarness.setState(host); await tick(); const list = document.querySelector<HTMLElement>('[role="grid"]')!; list.focus(); list.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true })); await tick(); assert(document.getElementById('script-22222222-2222-2222-2222-222222222222')?.getAttribute('aria-selected') === 'true', 'End did not select last row'); assert(list.getAttribute('aria-activedescendant') === 'script-22222222-2222-2222-2222-222222222222', 'Active descendant stale');
  });
  await check('preferences use true system option and preserve draft until explicit save', async () => {
    await window.fixtureHarness.clearCalls(); button('Preferências').click(); await until(() => !!document.getElementById('settings-theme'), 'Preferences missing'); const theme = document.getElementById('settings-theme') as HTMLSelectElement; assert([...theme.options].some(option => option.value === 'System' && option.text === 'Seguir o sistema'), 'Automatic system option absent'); theme.value = 'Dark'; theme.dispatchEvent(new Event('change', { bubbles: true })); await tick(); assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'saveSettings'), 'Draft applied without save'); button('Cancelar', document.querySelector('[role="dialog"]')!).click(); await tick();
  });
  results.push(...await runStartInTrayScenario());
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
    viewport.scrollIntoView({ block: 'center' }); const grid = document.querySelector<HTMLElement>('[role="grid"]')!; grid.focus(); grid.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true })); await tick(); assert(grid.getAttribute('aria-activedescendant') === 'script-' + host.snapshot!.scripts[1]!.id, 'Keyboard selection lost'); assert(viewport.getBoundingClientRect().top < workspace.getBoundingClientRect().bottom, 'List cannot scroll into reach');
    if (innerWidth <= 900) { button(text.hideDetails, inspector).click(); await tick(); assert(inspector.hidden, 'Details did not close'); verify(); button(text.details).click(); await tick(); assert(!inspector.hidden, 'Details did not reopen'); }
    noticeDetails.open = false; analysis.open = false; metadata.open = false; workspace.scrollTop = 0; await tick(); verify(); await window.fixtureHarness.resetResponses();
  });
  await check('semantic preference table preserves null, selection and native checkbox keyboard focus', async () => {
    button(text.scripts).click(); const search = document.querySelector<HTMLInputElement>('input[aria-label="Pesquisar scripts"]'); if (search) edit(search, '');
    await window.fixtureHarness.resetResponses(); await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, scripts: [{ ...host.snapshot!.scripts[0]!, runAtLogin: null }, host.snapshot!.scripts[1]!] } }); await tick();
    selectScript(host.snapshot!.scripts[0]!.id); await tick(); await window.fixtureHarness.clearCalls();
    const table = document.querySelector<HTMLTableElement>('table[role="grid"]')!;
    assert(table.querySelectorAll('thead th').length === 5 && table.querySelectorAll('tbody .script-row').length === 2, 'Semantic five-column table missing');
    assert(!table.querySelector('button input, [role="option"] input, [role="listbox"] input'), 'Checkbox nested inside interactive selection control');
    const first = document.getElementById('script-' + host.snapshot!.scripts[0]!.id)!;
    const login = first.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    assert(!login.checked && login.getAttribute('aria-label')?.includes(text.startupColumn) && login.getAttribute('aria-label')?.includes(host.snapshot!.scripts[0]!.friendlyName), 'Legacy null/accessibility label lost');
    assert((document.getElementById('draft-login') as HTMLInputElement).type === 'checkbox' && !(document.getElementById('draft-login') as HTMLInputElement).checked, 'Inspector startup is not unchecked nullable checkbox');
    const second = document.getElementById('script-' + host.snapshot!.scripts[1]!.id)!; const checkbox = second.querySelector<HTMLInputElement>('input[type="checkbox"]')!; checkbox.focus(); checkbox.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })); await tick();
    assert(first.getAttribute('aria-selected') === 'true' && document.activeElement === checkbox, 'Checkbox arrows changed selection or stole focus');
    assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'saveMetadata' || call.method === 'operate'), 'Passive null display/focus dispatched an effect');
  });
  await check('cross-row checkbox saves only snapshot preference and preserves selection and another row draft', async () => {
    await window.fixtureHarness.resetResponses(); await window.fixtureHarness.setState(host); await tick(); selectScript(host.snapshot!.scripts[0]!.id); await tick();
    const metadata = document.querySelector<HTMLDetailsElement>('.metadata-expander')!; metadata.open = true; edit(document.getElementById('draft-name') as HTMLInputElement, 'Rascunho de outra linha'); await tick();
    await window.fixtureHarness.setResponse('saveMetadata', { pending: true }); await window.fixtureHarness.clearCalls();
    const target = host.snapshot!.scripts[1]!; const row = document.getElementById('script-' + target.id)!; const checkbox = row.querySelector<HTMLInputElement>('input[type="checkbox"]')!; checkbox.click();
    await until(() => checkbox.disabled, 'Preference did not become pending/disabled'); await untilCall('saveMetadata'); const calls = await window.fixtureHarness.calls(); const write = calls.find(call => call.method === 'saveMetadata'); assert(write, 'Preference request absent');
    assert(write.args[0] === target.id && (write.args[1] as { friendlyName: string; runAtLogin: boolean }).friendlyName === target.friendlyName && (write.args[1] as { runAtLogin: boolean }).runAtLogin === true && write.args[2] === host.snapshot!.revision, 'Cross-row payload used selected draft or wrong revision');
    checkbox.click(); assert((await window.fixtureHarness.calls()).filter(call => call.method === 'saveMetadata').length === 1, 'Pending gesture wrote twice');
    await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, revision: '9007199254742001', scripts: [host.snapshot!.scripts[0]!, { ...target, runAtLogin: true }] } });
    await window.fixtureHarness.resolveResponse('saveMetadata', { value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null } }); await until(() => !checkbox.disabled, 'Preference did not settle');
    assert(checkbox.checked && document.getElementById('script-' + host.snapshot!.scripts[0]!.id)?.getAttribute('aria-selected') === 'true', 'Cross-row checkbox changed selection');
    assert(String((document.getElementById('draft-name') as HTMLInputElement).value) === 'Rascunho de outra linha', 'Other row dirty draft lost');
    assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'operate' || call.method === 'saveSettings'), 'Preference dispatched process/UAC/global settings'); await window.fixtureHarness.resetResponses();
  });
  await check('confirmed row admin choice rebases dirty manual draft so its later Save cannot undo preference', async () => {
    const base = { ...host, snapshot: { ...host.snapshot!, scripts: [{ ...host.snapshot!.scripts[0]!, runAtLogin: null, runAsAdministrator: false }, host.snapshot!.scripts[1]!] } };
    await window.fixtureHarness.setState(base); await tick(); selectScript(base.snapshot.scripts[0]!.id); await tick();
    const panel = document.querySelector('.inspector')!; const discard = button(text.discard, panel); if (!discard.disabled) { discard.click(); await tick(); }
    (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true; edit(document.getElementById('draft-name') as HTMLInputElement, 'Nome manual novo'); edit(document.getElementById('draft-description') as HTMLTextAreaElement, 'Texto manual novo'); await tick();
    await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveMetadata', { pending: true }); const row = document.getElementById('script-' + base.snapshot.scripts[0]!.id)!; const admin = row.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[1]!; admin.click(); await until(() => admin.disabled, 'Admin preference not pending');
    await untilCall('saveMetadata'); const request = (await window.fixtureHarness.calls()).find(call => call.method === 'saveMetadata')!; assert((request.args[1] as { runAtLogin: unknown }).runAtLogin === null && (request.args[1] as { friendlyName: string }).friendlyName === base.snapshot.scripts[0]!.friendlyName, 'Admin-only row write normalized null or saved dirty name');
    const persisted = { ...base, snapshot: { ...base.snapshot, revision: '9007199254742002', scripts: [{ ...base.snapshot.scripts[0]!, runAsAdministrator: true }, base.snapshot.scripts[1]!] } };
    await window.fixtureHarness.setState(persisted); await window.fixtureHarness.resolveResponse('saveMetadata', { value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null } }); await until(() => !admin.disabled, 'Admin preference did not settle');
    assert(admin.checked && String((document.getElementById('draft-name') as HTMLInputElement).value) === 'Nome manual novo' && String((document.getElementById('draft-description') as HTMLTextAreaElement).value) === 'Texto manual novo', 'Confirmed row choice replaced manual draft');
    await window.fixtureHarness.resetResponses(); await window.fixtureHarness.setResponse('saveMetadata', { pending: true }); await window.fixtureHarness.clearCalls(); button(text.save, panel).click();
    await until(() => button(text.save, panel).disabled, 'Manual Save not pending'); await untilCall('saveMetadata'); const saved = (await window.fixtureHarness.calls()).find(call => call.method === 'saveMetadata')!;
    assert((saved.args[1] as { runAsAdministrator: boolean; runAtLogin: unknown }).runAsAdministrator === true && (saved.args[1] as { runAtLogin: unknown }).runAtLogin === null, 'Old manual Save undid row choice');
    await window.fixtureHarness.setState({ ...persisted, snapshot: { ...persisted.snapshot, revision: '9007199254742003', scripts: [{ ...persisted.snapshot.scripts[0]!, friendlyName: 'Nome manual novo', description: 'Texto manual novo' }, persisted.snapshot.scripts[1]!] } });
    await window.fixtureHarness.resolveResponse('saveMetadata', { value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null } }); await tick(); await window.fixtureHarness.resetResponses();
  });
  await check('cancel/failure preserve preference and draft while Partial/Unknown read authority without retry', async () => {
    for (const result of ['CancelledBeforeDispatch', 'Failed', 'Partial', 'Unknown'] as const) {
      await window.fixtureHarness.resetResponses(); await window.fixtureHarness.setState(host); await tick(); selectScript(host.snapshot!.scripts[0]!.id); await tick();
      (document.querySelector('.metadata-expander') as HTMLDetailsElement).open = true; edit(document.getElementById('draft-name') as HTMLInputElement, 'Rascunho ' + result); await tick();
      await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveMetadata', { pending: true }); const row = document.getElementById('script-' + host.snapshot!.scripts[0]!.id)!; const checkbox = row.querySelector<HTMLInputElement>('input[type="checkbox"]')!; checkbox.click(); await until(() => checkbox.disabled, 'Preference did not serialize');
      await untilCall('saveMetadata'); if (result === 'Partial' || result === 'Unknown') await window.fixtureHarness.setState({ ...host, snapshot: { ...host.snapshot!, revision: result === 'Partial' ? '9007199254742010' : '9007199254742011', scripts: [{ ...host.snapshot!.scripts[0]!, runAtLogin: true }, host.snapshot!.scripts[1]!] } });
      await window.fixtureHarness.resolveResponse('saveMetadata', { value: { outcome: result, code: result === 'CancelledBeforeDispatch' ? 'Cancelled' : result === 'Failed' ? 'InvalidMetadata' : 'PendingOperation', detail: 'FIXTURE', correlationId: null } }); await until(() => !checkbox.disabled, 'Preference did not settle');
      const calls = await window.fixtureHarness.calls(); assert(calls.filter(call => call.method === 'saveMetadata').length === 1 && !calls.some(call => call.method === 'operate'), 'Preference result replayed or operated a script');
      assert(checkbox.checked === (result === 'Partial' || result === 'Unknown') && String((document.getElementById('draft-name') as HTMLInputElement).value) === 'Rascunho ' + result, 'Outcome lost authoritative value or manual intent');
      if (result === 'Partial' || result === 'Unknown') assert(calls.some(call => call.method === 'refresh') && calls.filter(call => call.method === 'getState').length >= 2, 'Uncertain result did not re-read authority');
      if (result === 'CancelledBeforeDispatch') assert(document.querySelector('.notice')?.textContent?.includes(text.preferenceCancelled), 'Cancellation was not announced');
    }
    await window.fixtureHarness.resetResponses();
  });
  for (const field of ['runAsAdministrator', 'runAtLogin'] as const) for (const original of field === 'runAtLogin' ? [false, true, null] : [false, true]) for (const outcome of ['Exception', 'Partial', 'Unknown'] as const) {
    await check(`${field} ${String(original)} unchanged after ${outcome} preserves same-field inspector draft and dirty state`, async () => {
      const requested = original !== true;
      const base = { ...host, snapshot: { ...host.snapshot!, scripts: [{ ...host.snapshot!.scripts[0]!, runAtLogin: null, runAsAdministrator: false, [field]: original }, host.snapshot!.scripts[1]!] } };
      await window.fixtureHarness.resetResponses(); await window.fixtureHarness.setState(base); await tick(); selectScript(base.snapshot.scripts[0]!.id); await tick();
      const panel = document.querySelector('.inspector')!; const discard = button(text.discard, panel); if (!discard.disabled) { discard.click(); await tick(); }
      (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true;
      const draft = field === 'runAtLogin' ? document.getElementById('draft-login') as HTMLInputElement : panel.querySelectorAll<HTMLInputElement>('.metadata-form input[type="checkbox"]')[1]!;
      draft.click(); await tick(); assert(draft.checked === requested && !button(text.save, panel).disabled, 'Same-field draft was not dirty before row attempt');
      await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
      const row = document.getElementById('script-' + base.snapshot.scripts[0]!.id)!; const checkbox = row.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[field === 'runAtLogin' ? 0 : 1]!;
      checkbox.click(); await untilCall('saveMetadata');
      await window.fixtureHarness.resolveResponse('saveMetadata', outcome === 'Exception' ? { error: 'FIXTURE write rejected before persistence' } : { value: { outcome, code: 'PendingOperation', detail: 'FIXTURE no persistence', correlationId: null } });
      await until(() => !checkbox.disabled, 'Unpersisted preference did not settle');
      assert(checkbox.checked === (original === true), 'Row did not retain authoritative preference');
      assert(draft.checked === requested, 'Unpersisted row attempt overwrote same-field inspector draft');
      assert(!button(text.save, panel).disabled && window.fixtureHarness.readGuard()?.dirtyDrafts === true, 'Unpersisted row attempt cleared dirty state');
      const calls = await window.fixtureHarness.calls(); assert(calls.filter(call => call.method === 'saveMetadata').length === 1 && !calls.some(call => call.method === 'operate' || call.method === 'saveSettings'), 'Unpersisted row attempt retried or dispatched another effect');
      if (field === 'runAsAdministrator') assert((calls.find(call => call.method === 'saveMetadata')!.args[1] as { runAtLogin: unknown }).runAtLogin === null && !(document.getElementById('draft-login') as HTMLInputElement).checked, 'Admin attempt normalized nullable login');
      button(text.discard, panel).click(); await tick(); await window.fixtureHarness.resetResponses();
    });
  }
  for (const field of ['runAsAdministrator', 'runAtLogin'] as const) await check(`${field} exception after persistence reconciles row choice while preserving unrelated draft`, async () => {
    const base = { ...host, snapshot: { ...host.snapshot!, scripts: [{ ...host.snapshot!.scripts[0]!, runAtLogin: null, runAsAdministrator: false }, host.snapshot!.scripts[1]!] } };
    await window.fixtureHarness.resetResponses(); await window.fixtureHarness.setState(base); await tick(); selectScript(base.snapshot.scripts[0]!.id); await tick();
    const panel = document.querySelector('.inspector')!; const discard = button(text.discard, panel); if (!discard.disabled) { discard.click(); await tick(); }
    (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true; edit(document.getElementById('draft-name') as HTMLInputElement, 'Preservar nome após exceção'); await tick();
    await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveMetadata', { pending: true }); const checkbox = document.getElementById('script-' + base.snapshot.scripts[0]!.id)!.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[field === 'runAtLogin' ? 0 : 1]!;
    checkbox.click(); await untilCall('saveMetadata');
    await window.fixtureHarness.setState({ ...base, snapshot: { ...base.snapshot, revision: '9007199254742090', scripts: [{ ...base.snapshot.scripts[0]!, [field]: true }, base.snapshot.scripts[1]!] } });
    await window.fixtureHarness.resolveResponse('saveMetadata', { error: 'FIXTURE response lost after persistence' }); await until(() => !checkbox.disabled, 'Persisted exception did not settle');
    const draft = field === 'runAtLogin' ? document.getElementById('draft-login') as HTMLInputElement : panel.querySelectorAll<HTMLInputElement>('.metadata-form input[type="checkbox"]')[1]!;
    assert(checkbox.checked && draft.checked && String((document.getElementById('draft-name') as HTMLInputElement).value) === 'Preservar nome após exceção' && !button(text.save, panel).disabled, 'Persisted exception lost row choice or unrelated draft');
    button(text.discard, panel).click(); await tick(); await window.fixtureHarness.resetResponses();
  });
  await check('admin plus login remains a manual-consent policy warning and failed existing guard never enables global startup', async () => {
    const state = { ...host, snapshot: { ...host.snapshot!, settings: { ...host.snapshot!.settings, launchManagerAtLogin: false }, scripts: [{ ...host.snapshot!.scripts[0]!, runAtLogin: true, runAsAdministrator: false }, host.snapshot!.scripts[1]!] } };
    await window.fixtureHarness.setState(state); await tick(); selectScript(state.snapshot.scripts[0]!.id); await tick(); const panel = document.querySelector('.inspector')!; const discard = button(text.discard, panel); if (!discard.disabled) { discard.click(); await tick(); }
    await window.fixtureHarness.clearCalls(); await window.fixtureHarness.setResponse('saveMetadata', { value: { outcome: 'Failed', code: 'StartupConsentRequired', detail: null, correlationId: null } });
    const admin = document.getElementById('script-' + state.snapshot.scripts[0]!.id)!.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[1]!; admin.click(); await tick();
    assert(!admin.checked && !(await window.fixtureHarness.calls()).some(call => call.method === 'saveSettings' || call.method === 'operate'), 'Admin checkbox bypassed existing startup guard');
    await window.fixtureHarness.setState({ ...state, snapshot: { ...state.snapshot, scripts: [{ ...state.snapshot.scripts[0]!, runAsAdministrator: true }, state.snapshot.scripts[1]!] } }); await tick();
    (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true; const reset = button(text.discard, panel); if (!reset.disabled) { reset.click(); await tick(); }
    assert(panel.textContent?.includes(text.adminLoginHint), 'Combined admin/login policy warning missing'); await window.fixtureHarness.resetResponses();
  });
  await check('administration preflight reads dirty drafts and dialogs without discarding either', async () => {
    const host = state(); await window.fixtureHarness.setState(host); selectScript(host.snapshot!.scripts[0]!.id); await tick();
    const discard = [...document.querySelectorAll<HTMLButtonElement>('button')].find(node => node.textContent?.trim() === text.discard); if (discard && !discard.disabled) discard.click(); await tick();
    assert(window.fixtureHarness.readGuard()?.dirtyDrafts === false, 'Clean draft incorrectly blocks handoff');
    edit(document.getElementById('draft-name') as HTMLInputElement, 'Rascunho preservado para elevação'); await tick();
    assert(window.fixtureHarness.readGuard()?.dirtyDrafts === true, 'Dirty draft omitted from preflight');
    button(text.discard).click(); await tick(); await window.fixtureHarness.openPreferences(); await tick();
    assert(window.fixtureHarness.readGuard()?.dialogOpen === true, 'Open preference dialog omitted from preflight'); button(text.cancel, document.querySelector('[role="dialog"]')!).click(); await tick();
  });
  await check('administration quiescence freezes all content inputs and sends no row save or operation', async () => {
    const host = state(); await window.fixtureHarness.setState(host); selectScript(host.snapshot!.scripts[0]!.id); await tick(); await window.fixtureHarness.clearCalls();
    const previous = (document.getElementById('draft-name') as HTMLInputElement).value;
    await window.fixtureHarness.setState({ ...host, administrationPending: true }); await tick();
    assert(document.querySelector('.app-shell')?.hasAttribute('inert'), 'Content remains interactive while authority is unknown');
    edit(document.getElementById('draft-name') as HTMLInputElement, 'Cannot change while quiescent');
    const checkbox = document.querySelector<HTMLInputElement>('tbody tr.script-row input[type="checkbox"]'); checkbox?.click(); await tick();
    assert((document.getElementById('draft-name') as HTMLInputElement).value === previous && !window.fixtureHarness.readGuard()?.dirtyDrafts, 'Quiescence changed draft');
    assert(!(await window.fixtureHarness.calls()).some(call => ['saveMetadata','operate','saveSettings'].includes(call.method)), 'Quiescence dispatched an effect');
    assert(button(text.checkAdministration) && document.querySelector('.authority-badge')?.textContent === text.authorityNormal, 'Unknown transfer has no explicit status recovery');
    await window.fixtureHarness.setState(host); await tick();
  });
  await check('authenticated host badge and per-process capability barriers are independent of preference', async () => {
    const host = state(); host.authorityMode = 'Administrator'; host.snapshot!.normalLaunchUnavailableReason = 'CapabilityUnverified';
    await window.fixtureHarness.setState(host); selectScript(host.snapshot!.scripts[0]!.id); await tick();
    assert(document.querySelector('.authority-badge')?.textContent === text.authorityAdministrator, 'High mode not shown from host proof');
    assert(button(operation('Start'), document.querySelector('.inspector')!).disabled, 'Normal launch capability barrier ignored');
    assert(!(document.querySelector('.inspector')?.textContent ?? '').includes(text.adminRequested), 'Preference false falsely reported admin request');
    await window.fixtureHarness.setState({ ...host, authorityMode: null }); await tick(); assert(document.querySelector('.authority-badge')?.textContent === text.authorityUnverified, 'Unknown host reported authenticated mode');
  });
  await check('root header is escaped multiline primary text; refresh/removal preserve editable manual fallback draft', async () => {
    button(text.scripts).click(); const search = document.querySelector<HTMLInputElement>('input[aria-label="Pesquisar scripts"]'); if (search) edit(search, '');
    const header = '<script>literal sem executar</script>\nSegunda linha — ' + 'palavra'.repeat(110);
    const withHeader: HostState = { ...host, snapshot: { ...host.snapshot!, scripts: [{ ...host.snapshot!.scripts[0]!, description: 'Manual persistida', headerDescription: header }, host.snapshot!.scripts[1]!] } };
    await window.fixtureHarness.setState(withHeader); await tick(); selectScript(withHeader.snapshot!.scripts[0]!.id); await tick(); await until(() => !!document.getElementById('draft-description'), 'Description editor missing');
    const panel = document.querySelector<HTMLElement>('.inspector')!; const discard = button(text.discard, panel); if (!discard.disabled) { discard.click(); await tick(); }
    const metadata = panel.querySelector<HTMLDetailsElement>('.metadata-expander')!; metadata.open = true;
    const manual = document.getElementById('draft-description') as HTMLTextAreaElement;
    assert(String(manual.value) === 'Manual persistida', 'Header overwrote manual metadata');
    assert(panel.querySelector('.script-description')?.children.length === 1 && !panel.querySelector('.description-guidance') && !panel.querySelector('.script-description h3'), 'Present header must show only its description');
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
    assert(panel.querySelector('.description-guidance')?.textContent?.includes(text.addDescriptionHint) && panel.querySelector('.description-guidance pre')?.textContent === text.descriptionHeaderExample, 'Absent header lacks localized syntax guidance');
    assert(!!(panel.querySelector('.description')!.compareDocumentPosition(panel.querySelector('.description-guidance')!) & Node.DOCUMENT_POSITION_FOLLOWING), 'Manual fallback must precede guidance');
    await window.fixtureHarness.setState({ ...withHeader, snapshot: { ...withHeader.snapshot!, scripts: [{ ...withHeader.snapshot!.scripts[0]!, description: '', headerDescription: null }, withHeader.snapshot!.scripts[1]!] } }); await tick();
    assert(!panel.querySelector('.description') && panel.querySelector('.description-guidance')?.textContent?.includes(text.descriptionRefreshHint) && manual.value === 'Manual em edição', 'Completely empty description must show guidance alone without replacing draft');
    assert(!(await window.fixtureHarness.calls()).some(call => call.method === 'saveMetadata'), 'Derived refresh persisted without explicit save');
    await window.fixtureHarness.setState(withHeader); await tick(); metadata.open = false; panel.scrollTop = 0;
  });
  return results;
}

// A focused isolated preview lets the same harness capture the absent-header state.
export async function previewDescription(): Promise<AssertionRecord[]> {
  const host = state(); host.snapshot!.scripts[0] = { ...host.snapshot!.scripts[0]!, description: 'Descrição manual preservada.', headerDescription: null };
  await window.fixtureHarness.setState(host); await window.fixtureHarness.setTheme({ ...light, dark: true });
  selectScript(host.snapshot!.scripts[0]!.id); await tick();
  const panel = document.querySelector('.inspector')!;
  assert(panel.querySelector('.description')?.textContent === 'Descrição manual preservada.' && panel.querySelector('.description-guidance pre')?.textContent === text.descriptionHeaderExample, 'Absent-header preview did not preserve manual/guidance');
  return [{ name: 'absent-header focused preview preserves manual and exact comment example', passed: true }];
}

export async function previewHotkeys(missing=false):Promise<AssertionRecord[]> {
  button(text.scripts).click();
  const host=state();host.snapshot!.scripts[0]={...host.snapshot!.scripts[0]!,friendlyName:'Atalhos de exemplo',path:'C:\\FIXTURE ISOLADA\\Caminho longo para testar a quebra de texto\\Projeto de exemplo\\arquivo de teste.ahk',description:'Fixture sintética. Nenhum script real é executado.',status:missing?'Missing':'Stopped',issues:missing?[{code:'MissingFile',detail:null,scriptId:null}]:[],hotkeys:[{chord:'!^#+u',normalizedChord:'!^#+u',origin:'Parsed',context:'Contexto de exemplo',sourcePath:null,line:1,isRemap:false,possibleConflict:true},{chord:'~<^>!LButton Up',normalizedChord:'~<^>!LButton Up',origin:'Parsed',context:null,sourcePath:null,line:3,isRemap:false,possibleConflict:false,declaredGesture:'DoubleClick'},{chord:'F13',normalizedChord:'F13',origin:'Parsed',context:null,sourcePath:null,line:5,isRemap:false,possibleConflict:false}]};
  await window.fixtureHarness.setState(host);await tick();selectScript(host.snapshot!.scripts[0]!.id);await tick();
  const discard=[...document.querySelectorAll<HTMLButtonElement>('button')].find(node=>node.textContent?.trim()===text.discard);if(discard&&!discard.disabled){discard.click();await tick();}
  document.querySelector<HTMLButtonElement>('.notice button')?.click();await tick();
  const panel=document.querySelector<HTMLElement>('.inspector')!;panel.scrollTop=missing?0:Math.max(0,(panel.querySelector('.hotkey-details') as HTMLElement)?.offsetTop-180);
  if(innerWidth<=900)panel.scrollIntoView({block:'start'});
  return [{name:'synthetic hotkeys and long path preview',passed:!document.documentElement.scrollWidth||document.documentElement.scrollWidth<=innerWidth}];
}

export async function focusRecovery():Promise<AssertionRecord[]> {
  await previewHotkeys(true);
  for(const label of [text.locateScript,text.remove]){
    const node=button(label,document.querySelector('.missing-file-notice')!);node.focus({preventScroll:true});node.scrollIntoView({block:'nearest',inline:'nearest',behavior:'instant'});await tick();
    const rect=node.getBoundingClientRect();assert(document.activeElement===node&&!node.disabled&&rect.width>0&&rect.left>=0&&rect.right<=innerWidth&&rect.top>=0&&rect.bottom<=innerHeight,'Recovery action unreachable at zoom: '+label);
  }
  document.querySelector('.missing-file-notice .file-actions')!.scrollIntoView({block:'center',behavior:'instant'});await tick();
  for(const node of document.querySelectorAll<HTMLButtonElement>('.missing-file-notice .file-actions button')){const rect=node.getBoundingClientRect();assert(rect.top>=0&&rect.bottom<=innerHeight,'Recovery buttons do not fit together after scrolling');}
  return [{name:'Localizar and Remover individually keyboard focused, wrapped and visible at200% zoom',passed:true}];
}

export async function checkPrimaryLabel():Promise<AssertionRecord[]> {
  const key=document.querySelector<HTMLElement>('.row-hotkey kbd')!;const range=document.createRange();range.selectNodeContents(key);
  const bounds=key.getBoundingClientRect(),lines=[...range.getClientRects()];
  assert(key.textContent==='Ctrl + Alt + Shift + Win + U'&&lines.length<=2&&lines.every(line=>line.bottom<=bounds.bottom+1),'Complete primary shortcut does not fit two visible lines at normal1240');
  return [{name:'1240px primary shortcut visibly retains all modifiers and U within two lines',passed:true}];
}

export async function runDeleteSelectionScopes():Promise<AssertionRecord[]> {
  const results:AssertionRecord[]=[];
  const filter=(value:string)=>{const node=document.querySelector<HTMLSelectElement>('.script-filters select')!;node.value=value;node.dispatchEvent(new Event('change',{bubbles:true}));};
  for(const scope of ['Running','Stopped'] as const){
    let waiting=false;const host=state();host.snapshot!.scripts[0]!.status=scope;
    try{
      button(text.scripts).click();await window.fixtureHarness.resetResponses();await window.fixtureHarness.setState(host);await tick();
      edit(input('Pesquisar scripts'),'');filter(scope);await tick();selectScript(host.snapshot!.scripts[0]!.id);await tick();
      const discard=[...document.querySelectorAll<HTMLButtonElement>('button')].find(node=>node.textContent?.trim()===text.discard);if(discard&&!discard.disabled){discard.click();await tick();}
      await window.fixtureHarness.setResponse('deleteScriptFile',{pending:true});await window.fixtureHarness.clearCalls();button(text.deleteFile,document.querySelector('.inspector')!).click();await untilCall('deleteScriptFile');waiting=true;
      const pending={...host,busy:true,snapshot:{...host.snapshot!,revision:'9007199254741100',scripts:[{...host.snapshot!.scripts[0]!,status:'Unknown' as const,process:null,issues:[{code:'PendingOperation',detail:null,scriptId:host.snapshot!.scripts[0]!.id}]},host.snapshot!.scripts[1]!]}};
      await window.fixtureHarness.setState(pending);await tick();
      assert(document.querySelector('.inspector-name')?.textContent===host.snapshot!.scripts[0]!.friendlyName,'Own Pending hid active delete Inspector under '+scope);
      assert(!(await window.fixtureHarness.calls()).some(call=>call.method==='updateSelection'&&call.args[0]===null),'Own Pending invalidated controller epoch under '+scope);
      await window.fixtureHarness.clearCalls();
      if(scope==='Running'){edit(input('Pesquisar scripts'),'no matching scope');await tick();assert((await window.fixtureHarness.calls()).some(call=>call.method==='updateSelection'&&call.args[0]===null),'Real query change did not invalidate delete scope');}
      else{selectScript(host.snapshot!.scripts[1]!.id);await tick();assert((await window.fixtureHarness.calls()).some(call=>call.method==='updateSelection'&&(call.args[0] as {scriptId?:string}|null)?.scriptId===host.snapshot!.scripts[1]!.id),'Real selection change did not reach controller');}
      await window.fixtureHarness.resolveResponse('deleteScriptFile',{value:{outcome:'Partial',code:'Cancelled',detail:'Execução encerrada.',correlationId:null}});waiting=false;
      await window.fixtureHarness.setState({...pending,busy:false});await tick();
      if(scope==='Stopped')assert(document.querySelector('.inspector-name')?.textContent===host.snapshot!.scripts[1]!.friendlyName,'Old delete completion cleared new selection');
      results.push({name:'delete own Pending retains '+scope+' scope; real user change and late cleanup preserve cancellation',passed:true});
    }catch(error){results.push({name:'delete own Pending '+scope+' scope',passed:false,detail:String(error)});}
    finally{if(waiting)await window.fixtureHarness.resolveResponse('deleteScriptFile',{value:{outcome:'Partial',code:'Cancelled',detail:'fixture cleanup',correlationId:null}});await window.fixtureHarness.setState(host);await tick();filter('All');edit(input('Pesquisar scripts'),'');await tick();}
  }
  return results;
}

export async function runStartInTrayScenario(): Promise<AssertionRecord[]> {
  const host=state();
  try {
    const initial={...host,snapshot:{...host.snapshot!,settings:{...host.snapshot!.settings,showWindowAtLogin:true}}};
    await window.fixtureHarness.setState(initial);await tick();await window.fixtureHarness.clearCalls();
    const openPreferences=async()=>{button(text.preferences).click();await until(()=>!!document.getElementById('settings-start-in-tray'),'Tray preference missing');await tick();};
    const tray=()=>document.getElementById('settings-start-in-tray') as HTMLInputElement;
    const login=()=>document.getElementById('settings-show-at-login') as HTMLInputElement;
    await openPreferences();assert(!tray().checked,'Tray startup default changed');
    assert(document.querySelector('label[for="settings-start-in-tray"]')?.textContent==='Iniciar na bandeja','Checkbox label differs');
    tray().click();await tick();assert(tray().checked&&login().checked&&login().disabled,'Login visibility precedence or retained value missing');
    assert(!(await window.fixtureHarness.calls()).some(call=>call.method==='saveSettings'),'Draft applied before save');
    button(text.cancel,document.querySelector('[role="dialog"]')!).click();await tick();await openPreferences();
    assert(!tray().checked&&!login().disabled,'Cancel changed preference');tray().click();await tick();
    await window.fixtureHarness.setResponse('saveSettings',{value:{outcome:'Failed',code:'StorageFailure',detail:'FIXTURE save failure',correlationId:null}});
    button(text.save,document.querySelector('[role="dialog"]')!).click();await until(()=>!!document.querySelector('[role="dialog"] .notice'),'Save failure absent');
    assert(tray().checked&&login().checked&&login().disabled,'Save failure lost draft');
    const calls=(await window.fixtureHarness.calls()).filter(call=>call.method==='saveSettings');
    const saved=calls[0]!.args[0] as ManagerSnapshot['settings'];
    assert(saved.startInTray&&!saved.launchManagerAtLogin&&saved.showWindowAtLogin,'Save altered independent startup settings');
    await window.fixtureHarness.setResponse('saveSettings',{value:{outcome:'Confirmed',code:'None',detail:null,correlationId:null}});
    button(text.save,document.querySelector('[role="dialog"]')!).click();await until(()=>!document.querySelector('[role="dialog"]'),'Confirmed save did not close');
    await window.fixtureHarness.setState({...initial,snapshot:{...initial.snapshot,settings:saved}});await tick();await openPreferences();
    assert(tray().checked,'Reopened saved preference differs');tray().click();await tick();assert(!login().disabled&&login().checked,'Disabling tray startup discarded login preference');
    button(text.cancel,document.querySelector('[role="dialog"]')!).click();await tick();await window.fixtureHarness.resetResponses();await window.fixtureHarness.setState(host);await tick();
    return [{name:'start in tray checkbox preserves cancel, failure, save and login preferences',passed:true}];
  } catch(error) { return [{name:'start in tray checkbox preserves cancel, failure, save and login preferences',passed:false,detail:String(error)}]; }
}
