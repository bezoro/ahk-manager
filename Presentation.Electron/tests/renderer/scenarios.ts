import type {
  AdministrationUiGuard,
  HostState,
  ManagerSnapshot,
  ScriptSnapshot,
  ThemeState,
  StartupRepairPreview,
} from '../../src/shared/contracts.js';
import {
  operation,
  outcome,
  problemCount,
  reason,
  text,
} from '../../src/renderer/resources/en-US.js';
import type { UpdateState } from '../../src/shared/updates.js';

declare global {
  interface Window {
    fixtureHarness: {
      setUpdates(state: UpdateState): Promise<void>;
      readGuard(): AdministrationUiGuard | null;
      setResponse(
        method: string,
        response: { value?: unknown; error?: string; pending?: boolean },
      ): Promise<void>;
      resolveResponse(method: string, response: { value?: unknown; error?: string }): Promise<void>;
      resetResponses(): Promise<void>;
      openPreferences(): Promise<void>;
      pressEnter(): Promise<void>;
      setState(state: HostState): Promise<void>;
      setTheme(theme: ThemeState): Promise<void>;
      calls(): Promise<Array<{ method: string; args: unknown[] }>>;
      clearCalls(): Promise<void>;
    };
  }
}
interface AssertionRecord {
  name: string;
  passed: boolean;
  detail?: string;
}
const tick = (): Promise<void> =>
  new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
async function until(predicate: () => boolean, label: string): Promise<void> {
  const deadline = performance.now() + 3000;
  do {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 20));
  } while (performance.now() < deadline);
  throw new Error(label);
}
function assert(value: unknown, label: string): asserts value {
  if (!value) throw new Error(label);
}
function button(label: string, root: ParentNode = document): HTMLButtonElement {
  const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(
    (node) => node.textContent?.trim() === label || node.getAttribute('aria-label') === label,
  );
  assert(found, 'Missing button: ' + label);
  return found;
}
function input(label: string): HTMLInputElement {
  const node = document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`);
  assert(node, 'Missing input: ' + label);
  return node;
}
function edit(node: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  const details = node.closest<HTMLDetailsElement>('details');
  if (details) details.open = true;
  const prototype =
    node instanceof HTMLTextAreaElement
      ? HTMLTextAreaElement.prototype
      : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(node, value);
  node.dispatchEvent(new Event('input', { bubbles: true }));
}
function selectScript(id: string): void {
  const row = document.getElementById('script-' + id);
  const button = row?.querySelector<HTMLButtonElement>('.row-select');
  assert(button, 'Missing selection button: ' + id);
  button.click();
}
function script(id: string, patch: Partial<ScriptSnapshot> = {}): ScriptSnapshot {
  return {
    id,
    friendlyName: 'Script ' + id,
    path: `C:\\ISOLATED FIXTURE\\${id}.ahk`,
    description: '',
    collectionId: '33333333-3333-3333-3333-333333333333',
    runAtLogin: false,
    runAsAdministrator: false,
    launchBlocked: false,
    status: 'Stopped',
    process: null,
    hotkeys: [],
    coverage: 'Partial',
    issues: [],
    ...patch,
  };
}
function state(): HostState {
  const snapshot: ManagerSnapshot = {
    revision: '9007199254741001',
    scripts: [
      script('11111111-1111-1111-1111-111111111111', {
        friendlyName: 'Test editor',
        hotkeys: [
          {
            chord: '^j',
            normalizedChord: '^j',
            origin: 'Manual',
            context: null,
            sourcePath: null,
            line: null,
            isRemap: false,
            possibleConflict: false,
          },
        ],
      }),
      script('22222222-2222-2222-2222-222222222222', {
        friendlyName: 'Test browser',
        collectionId: null,
      }),
    ],
    collections: [{ id: '33333333-3333-3333-3333-333333333333', name: 'Test work' }],
    settings: {
      interpreterPath: null,
      editorPath: null,
      editorArguments: [],
      launchManagerAtLogin: false,
      launchManagerAsAdministrator: true,
      showWindowAtLogin: false,
      startInTray: false,
      theme: 'System',
      closeBehavior: 'HideToTray',
      window: { x: 0, y: 0, width: 1240, height: 820, maximized: false, dpi: 96 },
    },
    activity: [],
    issues: [
      {
        code: 'PartialAnalysis',
        detail: 'ISOLATED FIXTURE: no real scripts, profiles, or effects.',
        scriptId: null,
      },
    ],
    readOnly: false,
    effectsAvailable: true,
    observedAt: '2026-10-23T00:00:00Z',
  };
  return {
    connection: 'ready',
    snapshot,
    error: null,
    busy: false,
    authorityMode: 'Normal',
    administrationPending: false,
  };
}
async function untilCall(method: string): Promise<void> {
  for (let attempt = 0; attempt < 100; attempt++) {
    if ((await window.fixtureHarness.calls()).some((call) => call.method === method)) return;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error('No fixture call: ' + method);
}
const light: ThemeState = {
  dark: false,
  highContrast: false,
  reducedMotion: false,
  accent: '#627c47',
  material: 'solid',
};

function startupRepairFixture(): StartupRepairPreview {
  return {
    canRepair: true,
    previousExecutable: 'V:\\ISOLATED FIXTURE\\Previous Manager\\Bezoro.AhkManager.exe',
    currentExecutable: 'C:\\ISOLATED FIXTURE\\OneDrive Manager\\Bezoro.AhkManager.exe',
    fingerprint: 'b'.repeat(64),
    reason: 'None',
    detail: 'The saved preference disables Manager startup. The current task will also be removed.',
  };
}
const unavailableStartupRepair: StartupRepairPreview = {
  canRepair: false,
  previousExecutable: null,
  currentExecutable: null,
  fingerprint: null,
  reason: 'None',
  detail: null,
};
const startupRepairStorage = {
  mode: 'portable',
  currentDataDirectory: 'C:\\ISOLATED FIXTURE\\Manager\\Data',
  defaultDataDirectory: 'C:\\ISOLATED FIXTURE\\Manager\\Data',
  pendingDataDirectory: null,
  canChange: true,
  error: null,
};

export async function runScenarios(): Promise<AssertionRecord[]> {
  const results: AssertionRecord[] = [];
  async function check(name: string, action: () => Promise<void>): Promise<void> {
    try {
      await action();
      results.push({ name, passed: true });
    } catch (error) {
      results.push({ name, passed: false, detail: String(error) });
    }
  }
  const previousDialog = document.querySelector('[role="dialog"]');
  if (previousDialog) button('Cancel', previousDialog).click();
  await window.fixtureHarness.resetResponses();
  button('Scripts').click();
  const oldSearch = document.querySelector<HTMLInputElement>('input[aria-label="Search scripts"]');
  if (oldSearch) edit(oldSearch, '');
  const host = state();
  await window.fixtureHarness.setState(host);
  await window.fixtureHarness.setTheme(light);
  await until(
    () => !!document.getElementById('script-11111111-1111-1111-1111-111111111111'),
    'Renderer did not show fixture rows',
  );
  await check('actual snapshot labels, startup heading and accessible list options', async () => {
    assert(document.querySelector('h1')?.textContent === 'Scripts', 'Page heading');
    assert(
      document.querySelector('.table-head')?.textContent?.includes('Start with Windows'),
      'Explicit startup heading',
    );
    assert(document.querySelectorAll('tbody tr.script-row').length === 2, 'Real fixture rows');
    assert(
      document.querySelector('.native-title>span')?.textContent === 'AHK Manager',
      'Native caption drag surface',
    );
  });
  await check(
    'global pending recovery remains visible and dispatches once with an empty or filtered catalog',
    async () => {
      for (const empty of [true, false]) {
        const current = {
          ...host,
          snapshot: {
            ...host.snapshot!,
            scripts: empty ? [] : host.snapshot!.scripts,
            effectsAvailable: false,
            issues: [
              {
                code: 'PendingOperation',
                detail: 'Interrupted continuation fixture.',
                scriptId: null,
              },
            ],
          },
        };
        await window.fixtureHarness.setState(current);
        await tick();
        if (!empty) {
          edit(input(text.search), 'no fixture results');
          await tick();
        }
        assert(
          document.querySelectorAll('.script-row').length === 0,
          'Recovery fixture must have no visible row',
        );
        const recover = button(text.recover, document.querySelector('.library-notice')!);
        assert(
          !recover.disabled && !recover.closest('details'),
          'Global recovery depends on selection or collapsed details',
        );
        await window.fixtureHarness.clearCalls();
        recover.click();
        await untilCall('recoverPending');
        await tick();
        const calls = await window.fixtureHarness.calls();
        assert(
          calls.filter((call) => call.method === 'recoverPending').length === 1 &&
            !calls.some((call) => call.method === 'operate'),
          'Recovery used a selected script or repeated dispatch',
        );
        if (!empty) {
          edit(input(text.search), '');
          await tick();
        }
      }
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'startup remains configurable through cancel, failure and confirmed save while Manager administration is required',
    async () => {
      const initial = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          settings: {
            ...host.snapshot!.settings,
            launchManagerAtLogin: true,
            launchManagerAsAdministrator: true,
          },
        },
      };
      await window.fixtureHarness.setState(initial);
      await tick();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.clearCalls();
      const openSettings = async (): Promise<HTMLElement> => {
        button(text.preferences).click();
        await until(
          () => !!document.getElementById('settings-administrator-policy'),
          'Administrator policy missing',
        );
        await tick();
        return document.querySelector<HTMLElement>('[role="dialog"]')!;
      };
      const administrationRequired = (): boolean =>
        dialog.textContent?.includes(text.managerAdministratorRequired) === true &&
        !dialog.querySelector('#settings-administrator');
      const login = (): HTMLInputElement =>
        document.getElementById('settings-launch-at-login') as HTMLInputElement;
      let dialog = await openSettings();
      assert(
        dialog.querySelector('.preference-section h3')?.textContent === text.startup &&
          administrationRequired() &&
          login().checked,
        'Startup priority or default preferences lost',
      );
      assert(
        !(dialog.querySelector('.preference-advanced') as HTMLDetailsElement).open &&
          dialog
            .querySelector('input[aria-label="' + text.editor + '"]')
            ?.getAttribute('placeholder')
            ?.includes('VS Code'),
        'Advanced arguments or automatic editor default lost',
      );
      login().click();
      await tick();
      assert(
        !login().checked &&
          administrationRequired() &&
          !(await window.fixtureHarness.calls()).some((call) => call.method === 'saveSettings'),
        'Startup draft changed administrator policy or persisted before save',
      );
      button(text.cancel, dialog).click();
      await tick();
      dialog = await openSettings();
      assert(administrationRequired() && login().checked, 'Cancelled startup draft persisted');
      login().click();
      await tick();
      await window.fixtureHarness.setResponse('saveSettings', {
        value: {
          outcome: 'Failed',
          code: 'StorageFailure',
          detail: 'Save failure fixture.',
          correlationId: null,
        },
      });
      button(text.save, dialog).click();
      await until(() => !!dialog.querySelector('.notice'), 'Save failure missing');
      assert(administrationRequired() && !login().checked, 'Failure discarded startup draft');
      await window.fixtureHarness.setResponse('saveSettings', {
        value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null },
      });
      button(text.save, dialog).click();
      await until(
        () => !document.querySelector('[role="dialog"]'),
        'Confirmed startup save did not close',
      );
      const saved = (await window.fixtureHarness.calls())
        .filter((call) => call.method === 'saveSettings')
        .at(-1)!.args[0] as ManagerSnapshot['settings'];
      assert(
        !saved.launchManagerAtLogin &&
          saved.launchManagerAsAdministrator &&
          saved.startInTray === initial.snapshot.settings.startInTray,
        'Saved startup choice or mandatory administrator policy was lost',
      );
      await window.fixtureHarness.setState({
        ...initial,
        snapshot: { ...initial.snapshot, settings: saved },
      });
      await tick();
      dialog = await openSettings();
      assert(administrationRequired() && !login().checked, 'Reopened persisted preferences differ');
      button(text.cancel, dialog).click();
      await tick();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'a startup save requiring Windows approval closes its dialog before requesting administration',
    async () => {
      await window.fixtureHarness.setState(host);
      await tick();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.clearCalls();
      button(text.preferences).click();
      await until(
        () => !!document.getElementById('settings-launch-at-login'),
        'Startup preference missing',
      );
      (document.getElementById('settings-launch-at-login') as HTMLInputElement).click();
      await tick();
      assert(
        !(await window.fixtureHarness.calls()).some(
          (call) => call.method === 'requestAdministration',
        ),
        'Editing the startup draft requested UAC',
      );
      await window.fixtureHarness.setResponse('saveSettings', {
        value: {
          outcome: 'Partial',
          code: 'ManualElevationRequired',
          detail: 'Preference saved; configure elevated startup.',
          correlationId: null,
        },
      });
      await window.fixtureHarness.setResponse('requestAdministration', {
        value: {
          outcome: 'CancelledBeforeDispatch',
          code: 'Cancelled',
          detail: null,
          correlationId: null,
        },
      });
      button(text.save, document.querySelector('[role="dialog"]')!).click();
      await untilCall('requestAdministration');
      await tick();
      assert(
        !document.querySelector('[role="dialog"]') &&
          window.fixtureHarness.readGuard()?.dialogOpen === false &&
          window.fixtureHarness.readGuard()?.pendingWork === false,
        'Save requested UAC with stale dialog or pending-work guard',
      );
      const calls = await window.fixtureHarness.calls();
      assert(
        calls.filter((call) => call.method === 'saveSettings').length === 1 &&
          calls.filter((call) => call.method === 'requestAdministration').length === 1,
        'Save/elevation dispatch repeated',
      );
      assert(
        document
          .querySelector('.main-column>.notice')
          ?.textContent?.includes(outcome('CancelledBeforeDispatch')),
        'Windows cancellation was hidden',
      );
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'legacy standard-Manager settings save the required administrator mode without enabling Windows startup',
    async () => {
      const legacy = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          settings: {
            ...host.snapshot!.settings,
            launchManagerAtLogin: false,
            launchManagerAsAdministrator: false,
          },
        },
      };
      await window.fixtureHarness.setState(legacy);
      await tick();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.clearCalls();
      button(text.preferences).click();
      await until(
        () => !!document.getElementById('settings-administrator-policy'),
        'Required administration explanation missing for a legacy profile',
      );
      const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
      assert(dialog, 'Legacy profile Preferences dialog missing');
      try {
        assert(
          dialog.textContent?.includes(text.managerAdministratorRequired) === true &&
            dialog.textContent?.includes(text.managerAdministratorHint) === true &&
            !dialog.querySelector('#settings-administrator') &&
            !(document.getElementById('settings-launch-at-login') as HTMLInputElement).checked,
          'A legacy standard preference exposed an impossible mode or enabled startup',
        );
        assert(
          !(await window.fixtureHarness.calls()).some((call) =>
            ['saveSettings', 'requestAdministration', 'operate', 'saveMetadata'].includes(
              call.method,
            ),
          ),
          'Opening Preferences caused an effect before an explicit save',
        );
        await window.fixtureHarness.setResponse('saveSettings', {
          value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null },
        });
        button(text.save, dialog).click();
        await until(() => !dialog.isConnected, 'Saving required administration did not finish');
        const calls = await window.fixtureHarness.calls();
        const saves = calls.filter((call) => call.method === 'saveSettings');
        const saved = saves[0]?.args[0] as ManagerSnapshot['settings'];
        assert(
          saves.length === 1 &&
            saved.launchManagerAsAdministrator &&
            !saved.launchManagerAtLogin &&
            JSON.stringify({ ...saved, launchManagerAsAdministrator: false }) ===
              JSON.stringify(legacy.snapshot.settings),
          'Required administration changed unrelated preferences or enabled Windows startup',
        );
        assert(
          !calls.some((call) =>
            ['requestAdministration', 'operate', 'saveMetadata'].includes(call.method),
          ),
          'Saving required administration replayed a launch or edited a script',
        );
      } finally {
        if (dialog.isConnected) button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
        await window.fixtureHarness.setState(host);
        await tick();
      }
    },
  );
  await check(
    'readable hotkeys agree across list Inspector page and raw/readable search with accessible details',
    async () => {
      const keys = ['!^#+u', '~LButton Up', 'F13'].map((chord, index) => ({
        chord,
        normalizedChord: chord,
        origin: 'Parsed' as const,
        context: 'fixture context',
        sourcePath: 'C:\\ISOLATED FIXTURE\\generic.ahk',
        line: index + 1,
        isRemap: false,
        possibleConflict: index === 0,
        declaredGesture: index === 1 ? ('DoubleClick' as const) : null,
      }));
      const current = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          scripts: [{ ...host.snapshot!.scripts[0]!, hotkeys: keys }, host.snapshot!.scripts[1]!],
        },
      };
      await window.fixtureHarness.setState(current);
      await tick();
      selectScript(current.snapshot.scripts[0]!.id);
      await tick();
      const label = 'Ctrl + Alt + Shift + Win + U';
      assert(
        document.querySelector('.row-hotkey kbd')?.textContent === label,
        'List label still raw',
      );
      assert(
        [...document.querySelectorAll('.inspector kbd')].some((node) => node.textContent === label),
        'Inspector label differs',
      );
      assert(
        [...document.querySelectorAll('.inspector kbd')].some((node) => node.textContent === 'F13'),
        'F13 changed',
      );
      const key = document.querySelector<HTMLElement>('.row-hotkey kbd')!;
      key.focus();
      assert(
        document.activeElement === key && !!key.getAttribute('aria-label'),
        'Readable instruction not keyboard reachable',
      );
      const details = document.querySelector<HTMLDetailsElement>('.inspector .hotkey-explanation')!;
      details.open = true;
      assert(details.textContent?.includes('!^#+u'), 'Raw syntax lost');
      assert(document.querySelector('.inspector .conflict-label'), 'Conflict badge lost');
      assert(
        document.querySelector('.script-row')!.getBoundingClientRect().height <= 70,
        'Readable label broke 68px virtualization',
      );
      button(text.hotkeys).click();
      await tick();
      assert(
        document.querySelector('.data-table kbd')?.textContent === label,
        'Hotkeys page label differs',
      );
      const search = input(text.searchOther);
      edit(search, 'Ctrl + Alt');
      await tick();
      assert(
        document.querySelectorAll('.data-table tbody tr').length === 1,
        'Readable search failed',
      );
      edit(search, '!^#+u');
      await tick();
      assert(document.querySelectorAll('.data-table tbody tr').length === 1, 'Raw search lost');
      edit(search, '');
      button(text.scripts).click();
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'missing file offers direct recovery/removal and dirty draft disables all catalog/file entry points',
    async () => {
      const row = {
        ...host.snapshot!.scripts[0]!,
        status: 'Missing' as const,
        issues: [{ code: 'MissingFile', detail: null, scriptId: null }],
      };
      const current = {
        ...host,
        snapshot: { ...host.snapshot!, scripts: [row, host.snapshot!.scripts[1]!] },
      };
      await window.fixtureHarness.setState(current);
      await tick();
      selectScript(row.id);
      await tick();
      const notice = document.querySelector('.missing-file-notice')!;
      assert(notice.textContent?.includes(row.path), 'Old path absent beside recovery');
      assert(
        button(text.locateScript, notice) && button(text.remove, notice),
        'Missing direct actions',
      );
      await window.fixtureHarness.clearCalls();
      button(text.locateScript, notice).click();
      await untilCall('locateScript');
      assert(
        (await window.fixtureHarness.calls()).filter((call) => call.method === 'locateScript')
          .length === 1,
        'Locate dispatch missing',
      );
      await tick();
      const name = document.getElementById('draft-name') as HTMLInputElement;
      document.querySelector<HTMLDetailsElement>('.metadata-expander')!.open = true;
      edit(name, 'Dirty file action');
      await tick();
      assert(
        button(text.locateScript, notice).disabled &&
          button(text.remove, notice).disabled &&
          notice.textContent?.includes(text.fileDraftGuard),
        'Dirty actions or explanation missing',
      );
      await window.fixtureHarness.clearCalls();
      document
        .querySelector('[role="grid"]')!
        .dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
      await tick();
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'removeScript'),
        'Delete bypassed dirty guard',
      );
      button(text.discard).click();
      await tick();
      await window.fixtureHarness.setState(host);
      await tick();
      assert(
        button(text.deleteFile, document.querySelector('.inspector')!),
        'Existing file delete action missing',
      );
    },
  );
  await check(
    'manual metadata draft, focus and caret survive live theme and snapshot updates',
    async () => {
      selectScript('11111111-1111-1111-1111-111111111111');
      await until(() => !!document.getElementById('draft-name'), 'Inspector missing');
      const details = document.querySelector<HTMLDetailsElement>('.metadata-expander')!;
      details.open = true;
      const name = document.getElementById('draft-name') as HTMLInputElement;
      const hotkeys = document.getElementById('draft-hotkeys') as HTMLTextAreaElement;
      edit(name, 'Draft name');
      edit(hotkeys, '^k\n\n');
      name.focus();
      name.setSelectionRange(2, 6);
      await tick();
      await window.fixtureHarness.setTheme({ ...light, dark: true });
      await window.fixtureHarness.setState({
        ...host,
        snapshot: { ...host.snapshot!, revision: '9007199254741002' },
      });
      await tick();
      assert(document.getElementById('draft-name') === name, 'Input remounted');
      assert(name.value === 'Draft name', 'Draft lost');
      assert(hotkeys.value === '^k\n\n', 'Raw textarea changed');
      assert(
        document.activeElement === name && name.selectionStart === 2 && name.selectionEnd === 6,
        'Focus/caret lost',
      );
      assert(
        document.querySelector('.app-root')?.getAttribute('data-dark') === 'true',
        'Live dark state not applied',
      );
      await window.fixtureHarness.setTheme(light);
      await tick();
      assert(name.value === 'Draft name', 'Returning light lost draft');
    },
  );
  await check(
    'same-selection collection refresh removes stale membership and preserves other edits',
    async () => {
      const changed = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          revision: '9007199254741003',
          collections: [{ id: '44444444-4444-4444-4444-444444444444', name: 'New collection' }],
        },
      };
      await window.fixtureHarness.setState(changed);
      await tick();
      const select = document.getElementById('draft-collection') as HTMLSelectElement;
      assert(select.value === '', 'Removed membership remained selected');
      assert(
        [...select.options].some((option) => option.textContent === 'New collection'),
        'New collection not reachable',
      );
      assert(
        (document.getElementById('draft-name') as HTMLInputElement).value === 'Draft name',
        'Unrelated draft reset',
      );
    },
  );
  await check(
    'search hides action target immediately and recoverable draft remains after restoring filter',
    async () => {
      await window.fixtureHarness.clearCalls();
      edit(input('Search scripts'), 'no results');
      await until(
        () => !document.getElementById('script-11111111-1111-1111-1111-111111111111'),
        'Search did not filter',
      );
      await tick();
      const calls = await window.fixtureHarness.calls();
      assert(
        calls.some((call) => call.method === 'updateSelection' && call.args[0] === null),
        'Hidden selection not invalidated',
      );
      assert(!document.getElementById('draft-name'), 'Hidden row still inspected');
      edit(input('Search scripts'), '');
      await until(
        () => !!document.getElementById('script-11111111-1111-1111-1111-111111111111'),
        'Search did not restore',
      );
      selectScript('11111111-1111-1111-1111-111111111111');
      await tick();
      assert(
        (document.getElementById('draft-name') as HTMLInputElement).value === 'Draft name',
        'Filtered draft lost',
      );
    },
  );
  await check(
    'unknown row remains inspectable with safe file actions and blocked process effects',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        snapshot: {
          ...host.snapshot!,
          revision: '9007199254741004',
          scripts: [
            { ...host.snapshot!.scripts[0]!, status: 'Unknown' },
            host.snapshot!.scripts[1]!,
          ],
        },
      });
      await tick();
      const panel = document.querySelector('.inspector')!;
      assert(
        button('Start', panel).disabled &&
          button('Stop', panel).disabled &&
          button('Restart', panel).disabled,
        'Unknown process controls enabled',
      );
      assert(
        !button('Edit script', panel).disabled && !button('Open folder', panel).disabled,
        'Safe unknown file actions unavailable',
      );
      assert(!!document.getElementById('draft-name'), 'Unknown details inaccessible');
    },
  );
  await check(
    'owner unavailable disables toolbar and context actions consistently without dispatch',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        snapshot: { ...host.snapshot!, revision: '9007199254741005', effectsAvailable: false },
      });
      await tick();
      await window.fixtureHarness.clearCalls();
      const row = document.getElementById('script-11111111-1111-1111-1111-111111111111')!;
      row.dispatchEvent(
        new MouseEvent('contextmenu', { bubbles: true, clientX: 400, clientY: 230 }),
      );
      await until(() => !!document.querySelector('.script-context'), 'Context menu absent');
      const menu = document.querySelector('.script-context')!;
      assert(button('Start', menu).disabled, 'Context Start enabled');
      assert(
        button('Start', document.querySelector('.inspector')!).disabled,
        'Inspector Start enabled',
      );
      assert(!button('Edit script', menu).disabled, 'Safe context Edit disabled');
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'operate'),
        'Merely opening menu dispatched',
      );
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    },
  );
  await check('keyboard list navigation selects actual stable row identity', async () => {
    await window.fixtureHarness.setState(host);
    await tick();
    const list = document.querySelector<HTMLElement>('[role="grid"]')!;
    list.focus();
    list.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    await tick();
    assert(
      document
        .getElementById('script-11111111-1111-1111-1111-111111111111')
        ?.getAttribute('aria-selected') === 'true',
      'End did not select last row',
    );
    assert(
      list.getAttribute('aria-activedescendant') === 'script-11111111-1111-1111-1111-111111111111',
      'Active descendant stale',
    );
  });
  await check(
    'preferences use true system option and preserve draft until explicit save',
    async () => {
      await window.fixtureHarness.clearCalls();
      button('Preferences').click();
      await until(() => !!document.getElementById('settings-theme'), 'Preferences missing');
      const theme = document.getElementById('settings-theme') as HTMLSelectElement;
      assert(
        [...theme.options].some(
          (option) => option.value === 'System' && option.text === 'Use system setting',
        ),
        'Automatic system option absent',
      );
      theme.value = 'Dark';
      theme.dispatchEvent(new Event('change', { bubbles: true }));
      await tick();
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'saveSettings'),
        'Draft applied without save',
      );
      button('Cancel', document.querySelector('[role="dialog"]')!).click();
      await tick();
    },
  );
  results.push(...(await runStartInTrayScenario()));
  await check(
    'portable data choices prepare a restart without renderer-supplied paths or lost preferences',
    async () => {
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
      await window.fixtureHarness.openPreferences();
      await until(
        () => !!document.querySelector('input[aria-label="Current data folder"]'),
        'Data location did not load',
      );
      const previous = input('Current data folder').value;
      await window.fixtureHarness.clearCalls();
      button('Change location…').click();
      await until(
        () =>
          !!document
            .querySelector('[aria-labelledby="settings-data-location-title"] [role="status"]')
            ?.textContent?.includes('D:\\Manager Data'),
        'The data move was not prepared',
      );
      assert(
        input('Current data folder').value === previous,
        'Preparing a move changed the active folder',
      );
      const calls = await window.fixtureHarness.calls();
      assert(
        calls.some((call) => call.method === 'chooseDataLocation' && call.args.length === 0),
        'The renderer supplied a data path',
      );
      button('Cancel move').click();
      await until(
        () =>
          !document
            .querySelector('[aria-labelledby="settings-data-location-title"] [role="status"]')
            ?.textContent?.includes('Prepared data location'),
        'The prepared move did not cancel',
      );
      const checkbox = document.getElementById('settings-start-in-tray') as HTMLInputElement;
      checkbox.click();
      await tick();
      assert(button('Change location…').disabled, 'An unsaved preference allowed a data restart');
      checkbox.click();
      await tick();
      await window.fixtureHarness.setResponse('chooseDataLocation', {
        error: 'Choose an empty data folder.',
      });
      button('Change location…').click();
      await until(
        () =>
          !!document
            .querySelector('[aria-labelledby="settings-data-location-title"] [role="alert"]')
            ?.textContent?.includes('Choose an empty data folder.'),
        'Data preparation errors were hidden',
      );
      assert(
        input('Current data folder').value === previous,
        'A failed move changed the active folder',
      );
      await window.fixtureHarness.resetResponses();
      button('Change location…').click();
      await until(
        () =>
          !!document
            .querySelector('[aria-labelledby="settings-data-location-title"] [role="status"]')
            ?.textContent?.includes('Prepared data location'),
        'The second move was not prepared',
      );
      button('Restart Manager').click();
      await until(
        () => !document.querySelector('[role="dialog"]'),
        'Restart did not close preferences',
      );
      await tick();
      assert(
        (await window.fixtureHarness.calls()).some(
          (call) => call.method === 'restartForDataMove' && call.args.length === 0,
        ),
        'Restart was not explicitly requested',
      );
      await window.ahkManager.cancelDataMove();
    },
  );
  await check(
    'activity retains two-script association and local friendly-name search',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        snapshot: {
          ...host.snapshot!,
          revision: '9007199254741006',
          activity: host.snapshot!.scripts.map((item) => ({
            id: item.id,
            timestamp: '2026-10-23T00:00:00Z',
            scriptId: item.id,
            operation: 'Start',
            outcome: 'Confirmed',
            code: 'None',
            detail: 'ISOLATED FIXTURE',
          })),
        },
      });
      button('Activity').click();
      await until(
        () => document.querySelectorAll('.activity-list article').length === 2,
        'Activity fixture records missing',
      );
      edit(input('Search'), 'Test browser');
      await tick();
      assert(
        document.querySelectorAll('.activity-list article').length === 1,
        'Friendly-name activity filter failed',
      );
      assert(
        document.querySelector('.activity-list article')?.textContent?.includes('Test browser'),
        'Script association missing',
      );
      button('Scripts').click();
      await tick();
    },
  );
  await check(
    'loaded-library help explains first-use migration without dispatching an unavailable operation',
    async () => {
      await window.fixtureHarness.clearCalls();
      button('Help').click();
      await until(() => !!document.querySelector('[role="dialog"]'), 'Help missing');
      const dialog = document.querySelector('[role="dialog"]')!;
      assert(
        button('Migrate WinUI profile…', dialog).disabled,
        'Loaded catalog advertised available migration',
      );
      assert(
        dialog.textContent?.includes('only on first launch, before loading the catalog'),
        'First-use restriction not explained',
      );
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'migrateProfile'),
        'Help dispatched migration',
      );
      button('Close', dialog).click();
      await tick();
    },
  );
  await check(
    'Help opens only the fixed logs folder and reports native errors inline even when the backend is unavailable',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        connection: 'failed',
        error: 'ISOLATED FIXTURE: backend unavailable',
      });
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('openLogs', { pending: true });
      button(text.help).click();
      await until(() => !!document.querySelector('[role="dialog"]'), 'Help missing');
      const dialog = document.querySelector('[role="dialog"]')!;
      const openLogs = button(text.openLogs, dialog);
      assert(!openLogs.disabled, 'Backend failure disabled diagnostic access');
      assert(dialog.textContent?.includes(text.diagnosticLogsHint), 'Automatic logs not explained');
      openLogs.click();
      await untilCall('openLogs');
      await until(() => openLogs.disabled, 'Pending logs request did not disable its button');
      openLogs.click();
      const calls = (await window.fixtureHarness.calls()).filter(
        (call) => call.method === 'openLogs',
      );
      assert(calls.length === 1 && calls[0]!.args.length === 0, 'Logs accepted a path or repeated');
      await window.fixtureHarness.resolveResponse('openLogs', {
        error: 'ISOLATED FIXTURE: Explorer could not open the folder',
      });
      await until(
        () => !!dialog.querySelector('[role="alert"]')?.textContent?.includes(text.openLogsFailed),
        'Native logs error was not reported inline',
      );
      assert(dialog.isConnected, 'Logs error closed Help');
      assert(
        document.querySelectorAll('[role="dialog"]').length === 1,
        'Opening logs added a confirmation dialog',
      );
      await until(() => !openLogs.disabled, 'Logs button was not restored after failure');
      await window.fixtureHarness.setResponse('openLogs', {});
      openLogs.click();
      await until(
        () => !dialog.querySelector('[role="alert"]') && !openLogs.disabled,
        'Successful retry did not clear the inline error',
      );
      assert(
        (await window.fixtureHarness.calls()).filter((call) => call.method === 'openLogs')
          .length === 2,
        'Explicit retry was not sent exactly once',
      );
      button(text.close, dialog).click();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'Help copies native diagnostic reports with exact modes, one pending action and inline feedback while the backend is unavailable',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        snapshot: null,
        connection: 'failed',
        error: 'ISOLATED FIXTURE: backend unavailable',
      });
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('copyDiagnostics', { pending: true });
      button(text.help).click();
      await until(() => !!document.querySelector('[role="dialog"]'), 'Help missing');
      const dialog = document.querySelector('[role="dialog"]')!;
      const copy = button(text.copyDiagnosticReport, dialog);
      const errors = button(text.copyRecentErrors, dialog);
      const open = button(text.openLogs, dialog);
      const save = button(text.saveDiagnosticArchive, dialog);
      assert(!copy.disabled && !errors.disabled, 'Backend failure disabled report copying');
      assert(
        dialog.textContent?.includes(text.copyDiagnosticsHint),
        'Report privacy not explained',
      );
      copy.click();
      errors.click();
      open.click();
      save.click();
      await untilCall('copyDiagnostics');
      await until(
        () => copy.disabled && errors.disabled && open.disabled && save.disabled,
        'Pending report did not disable diagnostic actions',
      );
      const pendingCalls = (await window.fixtureHarness.calls()).filter(
        (call) =>
          call.method === 'copyDiagnostics' ||
          call.method === 'openLogs' ||
          call.method === 'exportDiagnostics',
      );
      assert(
        pendingCalls.length === 1 &&
          pendingCalls[0]!.args.length === 1 &&
          pendingCalls[0]!.args[0] === 'recent',
        'Pending report repeated, changed mode or accepted renderer report contents',
      );
      await window.fixtureHarness.resolveResponse('copyDiagnostics', {
        error: 'ISOLATED FIXTURE: clipboard unavailable',
      });
      await until(
        () =>
          !!dialog
            .querySelector('[role="alert"]')
            ?.textContent?.includes(text.copyDiagnosticsFailed),
        'Copy failure was not reported inline',
      );
      assert(dialog.isConnected, 'Copy failure closed Help');
      assert(document.querySelectorAll('[role="dialog"]').length === 1, 'Copy added a dialog');
      await until(
        () => !copy.disabled && !errors.disabled,
        'Copy actions not restored after failure',
      );
      await window.fixtureHarness.setResponse('copyDiagnostics', {});
      copy.click();
      await until(
        () =>
          !!dialog
            .querySelector('[role="status"]')
            ?.textContent?.includes(text.diagnosticReportCopied) && !copy.disabled,
        'Successful retry did not confirm the clipboard result',
      );
      assert(!dialog.querySelector('[role="alert"]'), 'Successful retry retained the error');
      await window.fixtureHarness.setResponse('copyDiagnostics', { pending: true });
      errors.click();
      await until(() => errors.disabled, 'Error report did not enter its pending state');
      await window.fixtureHarness.resolveResponse('copyDiagnostics', {});
      await until(
        () =>
          !errors.disabled &&
          !!dialog
            .querySelector('[role="status"]')
            ?.textContent?.includes(text.diagnosticReportCopied),
        'Error report did not confirm copying',
      );
      const completedCalls = (await window.fixtureHarness.calls()).filter(
        (call) => call.method === 'copyDiagnostics',
      );
      assert(
        completedCalls.length === 3 &&
          completedCalls.map((call) => call.args[0]).join(',') === 'recent,recent,errors' &&
          completedCalls.every((call) => call.args.length === 1),
        'Report modes or retry counts changed',
      );
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'copyText'),
        'Renderer copied raw report contents',
      );
      button(text.close, dialog).click();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'Help exports archives without a backend and distinguishes cancellation, failure, complete and partial saves',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        snapshot: null,
        connection: 'failed',
        error: 'ISOLATED FIXTURE: backend unavailable',
      });
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('exportDiagnostics', { pending: true });
      button(text.help).click();
      await until(() => !!document.querySelector('[role="dialog"]'), 'Help missing');
      const dialog = document.querySelector('[role="dialog"]')!;
      const save = button(text.saveDiagnosticArchive, dialog);
      const others = [text.copyDiagnosticReport, text.copyRecentErrors, text.openLogs].map(
        (label) => button(label, dialog),
      );
      assert(!save.disabled, 'Backend failure disabled diagnostic export');
      assert(
        dialog.textContent?.includes(text.exportDiagnosticsHint),
        'Complete archive contents and collection issues not explained',
      );
      save.click();
      save.click();
      others.forEach((node) => node.click());
      await untilCall('exportDiagnostics');
      await until(
        () => save.disabled && others.every((node) => node.disabled),
        'Pending export did not disable all diagnostic actions',
      );
      const pending = (await window.fixtureHarness.calls()).filter((call) =>
        ['exportDiagnostics', 'copyDiagnostics', 'openLogs'].includes(call.method),
      );
      assert(
        pending.length === 1 && pending[0]!.args.length === 0,
        'Export repeated, accepted a renderer path or overlapped another action',
      );
      await window.fixtureHarness.resolveResponse('exportDiagnostics', { value: null });
      await until(() => !save.disabled, 'Cancelled export did not restore its action');
      assert(
        !dialog.querySelector('[role="alert"]') && !dialog.querySelector('[role="status"]'),
        'Cancelling the save dialog was reported as success or failure',
      );
      await window.fixtureHarness.setResponse('exportDiagnostics', { pending: true });
      save.click();
      await until(() => save.disabled, 'Export retry did not become pending');
      await window.fixtureHarness.resolveResponse('exportDiagnostics', {
        error: 'ISOLATED FIXTURE: archive destination unavailable',
      });
      await until(
        () =>
          !!dialog
            .querySelector('[role="alert"]')
            ?.textContent?.includes(text.exportDiagnosticsFailed) && !save.disabled,
        'Archive failure was not reported inline',
      );
      assert(document.querySelectorAll('[role="dialog"]').length === 1, 'Export added a dialog');
      await window.fixtureHarness.setResponse('exportDiagnostics', { pending: true });
      save.click();
      await until(() => save.disabled, 'Successful export retry did not become pending');
      await window.fixtureHarness.resolveResponse('exportDiagnostics', {
        value: { complete: true, issueCount: 0 },
      });
      await until(
        () =>
          !!dialog
            .querySelector('[role="status"]')
            ?.textContent?.includes(text.diagnosticArchiveSaved) && !save.disabled,
        'Saved archive did not report success',
      );
      assert(!dialog.querySelector('[role="alert"]'), 'Successful export retained the error');
      assert(
        !dialog
          .querySelector('[role="status"]')
          ?.textContent?.includes(text.diagnosticReportCopied),
        'Archive success claimed clipboard contents',
      );
      await window.fixtureHarness.setResponse('exportDiagnostics', { pending: true });
      save.click();
      await until(() => save.disabled, 'Partial export did not become pending');
      await window.fixtureHarness.resolveResponse('exportDiagnostics', {
        value: { complete: false, issueCount: 2 },
      });
      await until(
        () =>
          !!dialog
            .querySelector('[role="alert"]')
            ?.textContent?.includes(text.diagnosticArchivePartial) && !save.disabled,
        'Partial archive did not report its missing evidence',
      );
      assert(
        !dialog.querySelector('[role="status"]') &&
          dialog.querySelector('[role="alert"]')?.textContent?.includes('manifest.json'),
        'Partial archive claimed completeness or omitted the collection manifest',
      );
      const exports = (await window.fixtureHarness.calls()).filter(
        (call) => call.method === 'exportDiagnostics',
      );
      assert(
        exports.length === 4 && exports.every((call) => call.args.length === 0),
        'Export arguments or explicit retry count changed',
      );
      button(text.close, dialog).click();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'footer reports actual problem count, connection and pending request without claiming all-clear',
    async () => {
      await window.fixtureHarness.setState({
        ...host,
        busy: true,
        snapshot: { ...host.snapshot!, problemCount: 3 },
      });
      await tick();
      const footer = document.querySelector<HTMLElement>('.status-footer')!;
      assert(
        footer.textContent?.includes(problemCount(3)) && footer.textContent.includes(text.pending),
        'Real problem/busy summary missing',
      );
      assert(footer.getAttribute('aria-live') === 'polite', 'Footer status inaccessible');
      await window.fixtureHarness.setState({
        ...host,
        connection: 'failed',
        snapshot: { ...host.snapshot!, problemCount: 3 },
      });
      await tick();
      assert(footer.textContent?.includes('Library unavailable'), 'Connection failure hidden');
      assert(footer.scrollWidth <= footer.clientWidth, 'Footer horizontal overflow');
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'native-theme accessibility settings and layout stay bounded in current viewport',
    async () => {
      await window.fixtureHarness.setTheme({ ...light, highContrast: true, reducedMotion: true });
      await tick();
      const root = document.querySelector('.app-root')!;
      assert(
        root.getAttribute('data-contrast') === 'true' &&
          root.getAttribute('data-reduced-motion') === 'true',
        'Accessibility settings not applied',
      );
      assert(document.documentElement.scrollWidth <= innerWidth, 'Document horizontal overflow');
      assert(
        document.querySelector('.main-column')!.scrollWidth <=
          document.querySelector('.main-column')!.clientWidth + 1,
        'Main column horizontal overflow',
      );
      await window.fixtureHarness.setTheme(light);
    },
  );
  await check(
    'filter labels fit their controls and primary script actions remain in one group at this viewport',
    async () => {
      await window.fixtureHarness.setState(host);
      await tick();
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      const canvas = document.createElement('canvas');
      const context = canvas.getContext('2d');
      assert(context, 'Text measurement unavailable');
      for (const select of document.querySelectorAll<HTMLSelectElement>('.script-filters select')) {
        const style = getComputedStyle(select);
        context.font = style.font;
        const label = select.selectedOptions[0]?.textContent ?? '';
        assert(
          context.measureText(label).width +
            parseFloat(style.paddingLeft) +
            parseFloat(style.paddingRight) <=
            select.clientWidth + 1,
          'Selected filter label is clipped: ' + label,
        );
      }
      const actions = [
        ...document.querySelectorAll<HTMLButtonElement>('.primary-script-actions button'),
      ];
      assert(
        actions.length === 3 &&
          Math.max(...actions.map((node) => node.getBoundingClientRect().top)) -
            Math.min(...actions.map((node) => node.getBoundingClientRect().top)) <
            1,
        'Primary process controls wrapped into separate lines',
      );
      assert(
        actions.every((node) => node.scrollWidth <= node.clientWidth + 1),
        'Primary process label clipped',
      );
      const more = button(text.moreScriptActions, document.querySelector('.inspector')!);
      more.click();
      await tick();
      assert(
        document.querySelector('[role="menu"]')?.textContent?.includes(operation('TogglePause')) &&
          document
            .querySelector('[role="menu"]')
            ?.textContent?.includes(operation('ToggleSuspend')),
        'Secondary controls missing from menu',
      );
      more.click();
      await tick();
    },
  );
  const activeDialog = (): HTMLElement => {
    const node = document.querySelector<HTMLElement>('[role="dialog"]');
    assert(node, 'Active dialog missing');
    assert(!node.closest('[aria-hidden="true"]'), 'Dialog hidden from accessibility tree');
    return node;
  };
  const failure = {
    outcome: 'Failed',
    code: 'PersistenceFailed',
    detail: 'FIXTURE diagnostic detail',
    correlationId: null,
  };
  const open = async (kind: string): Promise<void> => {
    if (kind === 'preferences') button(text.preferences).click();
    else if (kind === 'collection') button(text.createCollection).click();
    else {
      await window.fixtureHarness.setResponse('previewImport', {
        value: {
          token: 'fixture-import',
          preview: {
            sourcePath: 'C:\\FIXTURE\\legacy.json',
            sourceHash: 'fixture',
            scriptCount: 2,
            canImport: true,
            issues: [],
          },
        },
      });
      button(text.help).click();
      await tick();
      button(text.importLegacy, activeDialog()).click();
    }
    await until(
      () =>
        !!document.querySelector('[role="dialog"]') &&
        (kind !== 'import' || !!document.querySelector('[role="dialog"] .file-value')),
      'Dialog did not open',
    );
    await tick();
  };
  for (const kind of ['preferences', 'collection', 'import']) {
    await check(
      kind + ' keeps failures, rejection and nonfinal outcomes in its dialog',
      async () => {
        const method =
          kind === 'preferences'
            ? 'saveSettings'
            : kind === 'collection'
              ? 'saveCollection'
              : 'commitImport';
        await open(kind);
        const dialog = activeDialog();
        const draft =
          kind === 'preferences'
            ? dialog.querySelector<HTMLTextAreaElement>('#settings-editor-arguments')
            : dialog.querySelector<HTMLInputElement>('#collection-name');
        if (draft) {
          edit(draft, 'FIXTURE edited draft');
          await tick();
        }
        const source = dialog.querySelector('.file-value')?.textContent;
        for (const value of ['Failed', 'Partial', 'Unknown', 'Accepted', 'Pending'] as const) {
          await window.fixtureHarness.setResponse(method, {
            value: { ...failure, outcome: value },
          });
          button(kind === 'import' ? text.import : text.save, dialog).click();
          await until(
            () => dialog.querySelector('.notice strong')?.textContent === outcome(value),
            value + ' feedback missing inside dialog',
          );
          assert(activeDialog() === dialog, 'Dialog remounted');
          assert(
            dialog.querySelector('[role="alert"]')?.textContent?.includes(reason(failure.code)),
            'Semantic reason missing',
          );
          assert(
            dialog.querySelector('.notice pre')?.textContent === failure.detail,
            'Technical detail missing',
          );
          if (draft) assert(draft.value === 'FIXTURE edited draft', 'Edited draft lost');
          else
            assert(
              dialog.querySelector('.file-value')?.textContent === source,
              'Import preview lost',
            );
        }
        await window.fixtureHarness.setResponse(method, { error: 'FIXTURE rejected request' });
        button(kind === 'import' ? text.import : text.save, dialog).click();
        await until(
          () =>
            dialog
              .querySelector('.notice pre')
              ?.textContent?.includes('FIXTURE rejected request') === true,
          'Thrown error not in dialog',
        );
        await window.fixtureHarness.setResponse(method, {
          value: { ...failure, outcome: 'CancelledBeforeDispatch' },
        });
        button(kind === 'import' ? text.import : text.save, dialog).click();
        await until(() => !dialog.querySelector('.notice'), 'Cancellation appeared as failure');
        assert(activeDialog() === dialog, 'Cancellation closed draft');
        await window.fixtureHarness.setResponse(method, {
          value: { ...failure, outcome: 'Confirmed', code: 'None' },
        });
        button(kind === 'import' ? text.import : text.save, dialog).click();
        await until(() => !document.querySelector('[role="dialog"]'), 'Confirmed did not close');
        await open(kind);
        assert(!activeDialog().querySelector('.notice'), 'Stale feedback after reopen');
        button(text.cancel, activeDialog()).click();
        await tick();
      },
    );
  }
  await check(
    'preferences copy startup refusal details with pending, failure and retry feedback without losing the draft',
    async () => {
      const detail =
        'Startup registration was preserved: task-source.\nNo startup entry was changed.';
      await window.fixtureHarness.setResponse('saveSettings', {
        value: { ...failure, outcome: 'Partial', code: 'StartupEntryConflict', detail },
      });
      await open('preferences');
      const dialog = activeDialog();
      const draft = dialog.querySelector<HTMLTextAreaElement>('#settings-editor-arguments')!;
      edit(draft, 'FIXTURE startup diagnostic draft');
      await tick();
      try {
        button(text.save, dialog).click();
        await until(
          () => dialog.querySelector('.notice pre')?.textContent === detail,
          'Startup refusal diagnostic detail missing',
        );
        (dialog.querySelector('.notice details') as HTMLDetailsElement).open = true;
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.setResponse('copyText', { pending: true });
        const copy = button(text.copyDetails, dialog);
        copy.click();
        await untilCall('copyText');
        await tick();
        assert(copy.disabled, 'Pending copy was not disabled');
        assert(
          dialog.querySelector('.copy-feedback[role="status"]')?.textContent === text.pending,
          'Pending clipboard request claimed success',
        );
        copy.click();
        assert(
          (await window.fixtureHarness.calls()).filter((call) => call.method === 'copyText')
            .length === 1,
          'Pending clipboard request dispatched twice',
        );
        await window.fixtureHarness.resolveResponse('copyText', {
          error: 'FIXTURE clipboard unavailable',
        });
        await until(
          () =>
            dialog.querySelector('.copy-feedback[role="alert"]')?.textContent ===
            text.copyDetailsFailed,
          'Clipboard failure was not reported inside Preferences',
        );
        assert(!copy.disabled, 'Clipboard failure prevented retry');
        assert(
          dialog.querySelector('.notice strong')?.textContent === outcome('Partial') &&
            dialog.querySelector('.notice pre')?.textContent === detail &&
            draft.value === 'FIXTURE startup diagnostic draft',
          'Clipboard failure replaced the startup refusal or discarded the draft',
        );
        await window.fixtureHarness.setResponse('copyText', { value: undefined });
        copy.click();
        await until(
          () => dialog.querySelector('.copy-feedback[role="status"]')?.textContent === text.copied,
          'Confirmed clipboard retry did not report success',
        );
        const calls = await window.fixtureHarness.calls();
        const copies = calls.filter((call) => call.method === 'copyText');
        assert(
          copies.length === 2 && copies.every((call) => call.args[0] === detail),
          'Clipboard did not receive the complete unchanged startup details',
        );
        assert(
          dialog.querySelector('.notice pre')?.textContent === detail &&
            draft.value === 'FIXTURE startup diagnostic draft' &&
            document.querySelectorAll('[role="dialog"]').length === 1 &&
            calls.every((call) => call.method !== 'saveSettings' && call.method !== 'operate'),
          'Copying details changed preferences, scripts or the existing diagnostic dialog',
        );
      } finally {
        await window.fixtureHarness
          .resolveResponse('copyText', { value: undefined })
          .catch(() => {});
        if (dialog.isConnected) button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      }
    },
  );
  await check('late preferences clipboard results do not leak into a reopened dialog', async () => {
    try {
      for (const response of [
        { value: undefined },
        { error: 'FIXTURE late clipboard rejection' },
      ]) {
        await window.fixtureHarness.setResponse('saveSettings', { value: failure });
        await open('preferences');
        let dialog = activeDialog();
        button(text.save, dialog).click();
        await until(() => !!dialog.querySelector('.notice pre'), 'Save failure detail missing');
        (dialog.querySelector('.notice details') as HTMLDetailsElement).open = true;
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.setResponse('copyText', { pending: true });
        button(text.copyDetails, dialog).click();
        await untilCall('copyText');
        button(text.cancel, dialog).click();
        await tick();
        await open('preferences');
        dialog = activeDialog();
        await window.fixtureHarness.resolveResponse('copyText', response);
        await tick();
        assert(
          dialog.isConnected &&
            !dialog.querySelector('.notice') &&
            !dialog.querySelector('.copy-feedback'),
          'A late clipboard result changed the reopened Preferences dialog',
        );
        button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      }
    } finally {
      await window.fixtureHarness.resolveResponse('copyText', { value: undefined }).catch(() => {});
      const dialog = document.querySelector('[role="dialog"]');
      if (dialog) button(text.cancel, dialog).click();
      await tick();
      await window.fixtureHarness.resetResponses();
    }
  });
  await check(
    'previous startup entry review requires explicit consent and saved preferences before one repair',
    async () => {
      const preview = startupRepairFixture();
      await window.fixtureHarness.setResponse('getStorageState', { value: startupRepairStorage });
      await window.fixtureHarness.setResponse('previewStartupRepair', { value: preview });
      await window.fixtureHarness.clearCalls();
      await open('preferences');
      const dialog = activeDialog();
      try {
        await until(
          () => !!dialog.querySelector('.startup-entry-review .startup-previous-executable'),
          'Previous startup entry review missing',
        );
        const review = dialog.querySelector<HTMLElement>('.startup-entry-review')!;
        assert(
          review.querySelector('.startup-previous-executable')?.textContent ===
            preview.previousExecutable &&
            review.querySelector('.startup-current-executable')?.textContent ===
              preview.currentExecutable &&
            review.textContent?.includes(preview.detail!),
          'Review omitted the verified paths or saved startup mode',
        );
        assert(
          !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'Opening Preferences changed a startup entry',
        );
        const action = button(text.removePreviousStartup, review);
        const draft = dialog.querySelector<HTMLTextAreaElement>('#settings-editor-arguments')!;
        edit(draft, 'FIXTURE unsaved startup repair draft');
        await tick();
        assert(
          action.disabled && review.textContent?.includes(text.startupRepairSaveFirst),
          'Unsaved preferences did not block the saved-mode repair',
        );
        action.click();
        await tick();
        assert(
          !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'Unsaved preferences dispatched startup repair',
        );
        edit(draft, '');
        await until(() => !action.disabled, 'Discarding draft did not restore review action');
        await window.fixtureHarness.setResponse('repairStartup', { pending: true });
        await window.fixtureHarness.clearCalls();
        action.click();
        await untilCall('repairStartup');
        await tick();
        assert(
          action.disabled &&
            button(text.save, dialog).disabled &&
            button(text.cancel, dialog).disabled &&
            window.fixtureHarness.readGuard()?.pendingWork === true,
          'Pending startup repair left mutation or dismissal controls active',
        );
        dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await tick();
        assert(dialog.isConnected, 'Escape dismissed an in-flight startup repair');
        action.click();
        const pending = (await window.fixtureHarness.calls()).filter(
          (call) => call.method === 'repairStartup',
        );
        assert(
          pending.length === 1 && pending[0]!.args[0] === preview.fingerprint,
          'Review did not bind exactly one repair to its returned fingerprint',
        );
        await window.fixtureHarness.setResponse('previewStartupRepair', {
          value: unavailableStartupRepair,
        });
        const detail =
          'The reviewed previous entry was removed. Saved startup preferences applied.';
        await window.fixtureHarness.resolveResponse('repairStartup', {
          value: { ...failure, outcome: 'Confirmed', code: 'None', detail },
        });
        await until(
          () =>
            !dialog.querySelector('.startup-entry-review') &&
            !button(text.cancel, dialog).disabled &&
            dialog.querySelector('.notice pre')?.textContent === detail,
          'Confirmed repair did not refresh the review and show its exact result',
        );
        assert(dialog.isConnected && draft.value === '', 'Repair closed or replaced Preferences');
        assert(
          !(await window.fixtureHarness.calls()).some((call) =>
            ['saveSettings', 'operate', 'requestAdministration'].includes(call.method),
          ),
          'Review added a save, script action or permission prompt',
        );
      } finally {
        await window.fixtureHarness
          .resolveResponse('repairStartup', { value: failure })
          .catch(() => {});
        if (dialog.isConnected) button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      }
    },
  );
  await check(
    'stale or unconfirmed startup repair stays visible without replay and exposes copyable failure details',
    async () => {
      const preview = startupRepairFixture();
      await window.fixtureHarness.setResponse('getStorageState', { value: startupRepairStorage });
      await window.fixtureHarness.setResponse('previewStartupRepair', { value: preview });
      await open('preferences');
      const dialog = activeDialog();
      try {
        await until(
          () => !!dialog.querySelector('.startup-previous-executable'),
          'Startup review missing',
        );
        await window.fixtureHarness.clearCalls();
        const changed = { ...preview, fingerprint: 'c'.repeat(64) };
        await window.fixtureHarness.setResponse('previewStartupRepair', { value: changed });
        const detail = 'Windows startup changed after review. The entry was preserved.';
        await window.fixtureHarness.setResponse('repairStartup', {
          value: { ...failure, code: 'StaleSelection', detail },
        });
        button(text.removePreviousStartup, dialog).click();
        await until(
          () =>
            dialog.querySelector('.notice pre')?.textContent === detail &&
            !button(text.removePreviousStartup, dialog).disabled,
          'Stale review result did not retain Preferences and refresh the preview',
        );
        assert(
          (await window.fixtureHarness.calls()).filter((call) => call.method === 'repairStartup')
            .length === 1,
          'A changed entry caused an automatic retry',
        );
        const rejection = 'FIXTURE startup repair result unavailable';
        await window.fixtureHarness.setResponse('repairStartup', { error: rejection });
        button(text.removePreviousStartup, dialog).click();
        await until(
          () =>
            dialog
              .querySelector('.startup-entry-review .notice pre')
              ?.textContent?.includes(rejection) === true,
          'Unconfirmed repair failure was hidden',
        );
        const review = dialog.querySelector<HTMLElement>('.startup-entry-review')!;
        const renderedDetail = review.querySelector('.notice pre')!.textContent!;
        assert(
          review.textContent?.includes(text.startupRepairUnconfirmed) &&
            ![...review.querySelectorAll('button')].some(
              (node) => node.textContent?.trim() === text.removePreviousStartup,
            ),
          'Unconfirmed repair still offers the old token or claims success',
        );
        (review.querySelector('.notice details') as HTMLDetailsElement).open = true;
        await window.fixtureHarness.setResponse('copyText', { error: 'FIXTURE clipboard denied' });
        button(text.copyDetails, review).click();
        await until(
          () => review.querySelector('.copy-feedback')?.textContent === text.copyDetailsFailed,
          'Review error clipboard failure was not reported locally',
        );
        await window.fixtureHarness.setResponse('copyText', { value: undefined });
        button(text.copyDetails, review).click();
        await until(
          () => review.querySelector('.copy-feedback')?.textContent === text.copied,
          'Review error clipboard retry did not confirm success',
        );
        assert(
          review.querySelector('.notice pre')?.textContent === renderedDetail,
          'Copying review error replaced its diagnostic context',
        );
        const copies = (await window.fixtureHarness.calls()).filter(
          (call) => call.method === 'copyText',
        );
        assert(
          copies.length === 2 && copies.every((call) => call.args[0] === renderedDetail),
          'Clipboard did not receive the complete rendered startup repair error',
        );
        button(text.reviewStartup, review).click();
        await until(
          () => !!dialog.querySelector('.startup-previous-executable'),
          'Readonly review retry did not restore the current preview',
        );
        assert(
          (await window.fixtureHarness.calls()).filter((call) => call.method === 'repairStartup')
            .length === 2,
          'Readonly review retried the startup mutation',
        );
      } finally {
        button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      }
    },
  );
  await check(
    'unavailable, incomplete or late startup reviews never authorize an unreviewed removal',
    async () => {
      await window.fixtureHarness.setResponse('getStorageState', { value: startupRepairStorage });
      try {
        for (const value of [
          unavailableStartupRepair,
          { ...startupRepairFixture(), previousExecutable: null },
        ]) {
          await window.fixtureHarness.setResponse('previewStartupRepair', { value });
          await window.fixtureHarness.clearCalls();
          await open('preferences');
          const dialog = activeDialog();
          await untilCall('previewStartupRepair');
          await tick();
          assert(
            ![...dialog.querySelectorAll('button')].some(
              (node) => node.textContent?.trim() === text.removePreviousStartup,
            ) &&
              !(await window.fixtureHarness.calls()).some(
                (call) => call.method === 'repairStartup',
              ),
            'An unavailable or incomplete preview authorized removal',
          );
          button(text.cancel, dialog).click();
          await tick();
          await window.fixtureHarness.resetResponses();
        }
        await window.fixtureHarness.setResponse('previewStartupRepair', { pending: true });
        await window.fixtureHarness.clearCalls();
        await open('preferences');
        await untilCall('previewStartupRepair');
        button(text.cancel, activeDialog()).click();
        await tick();
        await window.fixtureHarness.setResponse('previewStartupRepair', {
          value: unavailableStartupRepair,
        });
        await open('preferences');
        const current = activeDialog();
        await window.fixtureHarness.resolveResponse('previewStartupRepair', {
          value: startupRepairFixture(),
        });
        await tick();
        assert(
          !current.querySelector('.startup-entry-review') &&
            !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'A late readonly review changed the reopened Preferences dialog',
        );
        button(text.cancel, current).click();
        await tick();
      } finally {
        await window.fixtureHarness
          .resolveResponse('previewStartupRepair', { value: unavailableStartupRepair })
          .catch(() => {});
        const dialog = document.querySelector('[role="dialog"]');
        if (dialog) button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      }
    },
  );
  await check(
    'prepared data moves and read-only preferences cannot dispatch startup repair',
    async () => {
      try {
        await window.fixtureHarness.setResponse('getStorageState', {
          value: {
            ...startupRepairStorage,
            pendingDataDirectory: 'D:\\ISOLATED FIXTURE\\Prepared Manager Data',
            canChange: false,
          },
        });
        await window.fixtureHarness.setResponse('previewStartupRepair', {
          value: startupRepairFixture(),
        });
        await window.fixtureHarness.clearCalls();
        await open('preferences');
        let dialog = activeDialog();
        await until(
          () => !!dialog.querySelector('.startup-previous-executable'),
          'Startup review did not load alongside prepared data state',
        );
        const action = button(text.removePreviousStartup, dialog);
        assert(
          action.disabled && dialog.textContent?.includes(text.startupRepairFinishDataMove),
          'A prepared data move left startup repair enabled or unexplained',
        );
        action.click();
        await tick();
        assert(
          !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'Prepared data state dispatched startup mutation',
        );
        button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
        await window.fixtureHarness.setState({
          ...host,
          snapshot: { ...host.snapshot!, readOnly: true },
        });
        await window.fixtureHarness.setResponse('previewStartupRepair', {
          value: startupRepairFixture(),
        });
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.openPreferences();
        await until(
          () => !!document.querySelector('[role="dialog"]'),
          'Read-only Preferences did not open',
        );
        await tick();
        dialog = activeDialog();
        assert(
          !dialog.querySelector('.startup-entry-review') &&
            button(text.save, dialog).disabled &&
            !(await window.fixtureHarness.calls()).some((call) =>
              ['previewStartupRepair', 'repairStartup'].includes(call.method),
            ),
          'Read-only preferences requested startup effects or presented a repair action',
        );
        button(text.cancel, dialog).click();
        await tick();
      } finally {
        const dialog = document.querySelector('[role="dialog"]');
        if (dialog) button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
        await window.fixtureHarness.setState(host);
        await tick();
      }
    },
  );
  await check(
    'startup repair waits for verified data state and remains blocked after a storage read failure',
    async () => {
      try {
        await window.fixtureHarness.setResponse('getStorageState', { pending: true });
        await window.fixtureHarness.setResponse('previewStartupRepair', {
          value: startupRepairFixture(),
        });
        await window.fixtureHarness.clearCalls();
        await open('preferences');
        let dialog = activeDialog();
        await until(
          () => !!dialog.querySelector('.startup-previous-executable'),
          'Readonly startup review did not load while data state was pending',
        );
        let action = button(text.removePreviousStartup, dialog);
        assert(
          action.disabled &&
            dialog.querySelector('.startup-storage-state')?.textContent ===
              text.startupRepairCheckingData,
          'Unknown data state enabled repair or omitted the loading explanation',
        );
        action.click();
        await tick();
        assert(
          !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'Unknown data state dispatched startup mutation',
        );
        await window.fixtureHarness.resolveResponse('getStorageState', {
          value: startupRepairStorage,
        });
        await until(() => !action.disabled, 'Verified data state did not enable explicit repair');
        assert(
          !dialog.querySelector('.startup-storage-state') &&
            !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'Storage verification replayed the disabled action automatically',
        );
        button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
        await window.fixtureHarness.setResponse('getStorageState', {
          error: 'FIXTURE data location unavailable',
        });
        await window.fixtureHarness.setResponse('previewStartupRepair', {
          value: startupRepairFixture(),
        });
        await window.fixtureHarness.clearCalls();
        await open('preferences');
        dialog = activeDialog();
        await until(
          () =>
            !!dialog.querySelector('.startup-previous-executable') &&
            dialog.querySelector('.startup-storage-state')?.textContent ===
              text.startupRepairDataUnavailable,
          'Rejected data state omitted its repair explanation',
        );
        action = button(text.removePreviousStartup, dialog);
        assert(action.disabled, 'Failed data verification left startup repair enabled');
        action.click();
        await tick();
        assert(
          !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
          'Failed data verification dispatched startup mutation',
        );
        button(text.cancel, dialog).click();
        await tick();
      } finally {
        await window.fixtureHarness
          .resolveResponse('getStorageState', { value: startupRepairStorage })
          .catch(() => {});
        const dialog = document.querySelector('[role="dialog"]');
        if (dialog) button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      }
    },
  );
  await check(
    'picker, startup feedback and late completions respect preferences sessions',
    async () => {
      await open('preferences');
      let dialog = activeDialog();
      const draft = dialog.querySelector<HTMLTextAreaElement>('#settings-editor-arguments')!;
      edit(draft, 'FIXTURE preserved');
      await tick();
      await window.fixtureHarness.setResponse('pickInterpreter', {
        error: 'FIXTURE picker failure',
      });
      button(
        text.choose,
        dialog.querySelector('[aria-labelledby="settings-interpreter-title"]')!,
      ).click();
      await until(
        () =>
          dialog.querySelector('.notice pre')?.textContent?.includes('FIXTURE picker failure') ===
          true,
        'Picker error hidden',
      );
      assert(draft.value === 'FIXTURE preserved', 'Picker reset draft');
      await window.fixtureHarness.setResponse('pickInterpreter', { value: null });
      button(
        text.choose,
        dialog.querySelector('[aria-labelledby="settings-interpreter-title"]')!,
      ).click();
      await until(
        () =>
          !dialog.querySelector('.notice') &&
          !button(
            text.choose,
            dialog.querySelector('[aria-labelledby="settings-interpreter-title"]')!,
          ).matches(':disabled'),
        'Null picker reported failure',
      );
      await window.fixtureHarness.setResponse('transferStartup', {
        value: { ...failure, outcome: 'Partial' },
      });
      button(text.transferStartup, dialog).click();
      await until(
        () => dialog.querySelector('.notice strong')?.textContent === outcome('Partial'),
        'Transfer result hidden',
      );
      for (const response of [
        { value: 'C:\\FIXTURE\\late.exe' },
        { error: 'FIXTURE late picker rejection' },
      ]) {
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.setResponse('pickInterpreter', { pending: true });
        button(
          text.choose,
          dialog.querySelector('[aria-labelledby="settings-interpreter-title"]')!,
        ).click();
        await untilCall('pickInterpreter');
        button(text.cancel, dialog).click();
        await tick();
        await window.fixtureHarness.openPreferences();
        await tick();
        dialog = activeDialog();
        await window.fixtureHarness.resolveResponse('pickInterpreter', response);
        await tick();
        assert(!dialog.querySelector('.notice'), 'Late completion leaked to reopened dialog');
        assert(
          dialog.querySelector<HTMLInputElement>('input[aria-label="' + text.interpreter + '"]')
            ?.value === '',
          'Late picker populated reopened draft',
        );
      }
      button(text.cancel, dialog).click();
      await tick();
    },
  );
  await check(
    'pending dialog save blocks duplicate dispatch and waits for completion',
    async () => {
      await open('preferences');
      const dialog = activeDialog();
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('saveSettings', { pending: true });
      button(text.save, dialog).click();
      await untilCall('saveSettings');
      await tick();
      assert(
        button(text.save, dialog).disabled && button(text.cancel, dialog).disabled,
        'Pending controls not guarded',
      );
      button(text.save, dialog).click();
      assert(
        (await window.fixtureHarness.calls()).filter((call) => call.method === 'saveSettings')
          .length === 1,
        'Duplicate dispatch',
      );
      assert(!dialog.querySelector('.notice.success'), 'Pending claimed success');
      await window.fixtureHarness.resolveResponse('saveSettings', { value: failure });
      await until(() => !!dialog.querySelector('.notice'), 'Pending result absent');
      button(text.cancel, dialog).click();
      await tick();
    },
  );
  await check(
    'native preferences replaces another modal and ignores its late completion',
    async () => {
      for (const dismiss of ['cancel', 'escape']) {
        button(text.help).click();
        await tick();
        const help = activeDialog();
        assert(help.textContent?.includes(text.helpDescription), 'Help did not open');
        await window.fixtureHarness.openPreferences();
        await until(
          () => !!document.getElementById('settings-theme'),
          'Preferences did not replace Help',
        );
        const preferences = activeDialog();
        assert(
          document.querySelectorAll('[role="dialog"]').length === 1 && !help.isConnected,
          'Help remained behind Preferences',
        );
        if (dismiss === 'cancel') button(text.cancel, preferences).click();
        else
          preferences.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await until(
          () => !document.querySelector('[role="dialog"]'),
          'Help stranded after Preferences closed',
        );
        await tick();
        assert(
          !document.activeElement?.closest('[aria-hidden="true"],[inert]'),
          'Focus remained in hidden modal content',
        );
        const trigger = button(text.preferences);
        trigger.focus();
        assert(
          document.activeElement === trigger && !trigger.closest('[aria-hidden="true"],[inert]'),
          'Navigation focus unavailable',
        );
      }
      for (const response of [
        { value: failure },
        { value: { ...failure, outcome: 'Confirmed' } },
        { error: 'FIXTURE late collection rejection' },
      ]) {
        await open('collection');
        const collection = activeDialog();
        edit(
          collection.querySelector<HTMLInputElement>('#collection-name')!,
          'FIXTURE pending collection',
        );
        await tick();
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.setResponse('saveCollection', { pending: true });
        button(text.save, collection).click();
        await untilCall('saveCollection');
        await window.fixtureHarness.openPreferences();
        await until(
          () => !!document.getElementById('settings-theme'),
          'Native preferences did not replace modal',
        );
        const preferences = activeDialog();
        assert(
          document.querySelectorAll('[role="dialog"]').length === 1 &&
            !document.getElementById('collection-name'),
          'Two modal surfaces active',
        );
        await window.fixtureHarness.resolveResponse('saveCollection', response);
        await until(
          () => !button(text.cancel, preferences).disabled,
          'Pending completion did not release busy state',
        );
        await tick();
        assert(
          activeDialog() === preferences && !preferences.querySelector('.notice'),
          'Unrelated completion changed preferences',
        );
        button(text.cancel, preferences).click();
        await tick();
      }
    },
  );
  await check(
    'list retains usable rows, scrolling, keyboard selection and separate inspector',
    async () => {
      await window.fixtureHarness.setState(host);
      await tick();
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      const metadata = document.querySelector<HTMLDetailsElement>('.metadata-expander')!;
      metadata.open = true;
      edit(document.getElementById('draft-name') as HTMLInputElement, 'FIXTURE layout draft');
      await tick();
      await window.fixtureHarness.setResponse('saveMetadata', {
        value: { ...failure, detail: 'FIXTURE expanded diagnostic\n'.repeat(35) },
      });
      button(text.save, document.querySelector('.inspector')!).click();
      await until(() => !!document.querySelector('.main-column>.notice'), 'Layout notice missing');
      const workspace = document.querySelector<HTMLElement>('.workspace')!,
        viewport = document.querySelector<HTMLElement>('.script-viewport')!,
        inspector = document.querySelector<HTMLElement>('.inspector')!;
      const verify = (): void => {
        const list = viewport.getBoundingClientRect(),
          filters = document.querySelector('.script-filters')!.getBoundingClientRect(),
          details = inspector.getBoundingClientRect();
        assert(viewport.clientHeight >= 106, 'No usable row area');
        assert(list.top >= filters.bottom - 1, 'Filters overlap rows');
        if (innerWidth <= 900 && !inspector.hidden) {
          assert(details.top >= list.bottom - 1, 'Inspector overlaps list');
          assert(
            workspace.scrollHeight > workspace.clientHeight ||
              details.bottom <= workspace.getBoundingClientRect().bottom + 1,
            'Inspector unreachable',
          );
        }
        assert(
          document.querySelector('.script-row')!.getBoundingClientRect().height >= 60,
          'Row collapsed',
        );
      };
      verify();
      const noticeDetails = document.querySelector<HTMLDetailsElement>(
        '.main-column>.notice details',
      )!;
      noticeDetails.open = true;
      const analysis = document.querySelector<HTMLDetailsElement>('.library-notice details')!;
      analysis.open = true;
      await tick();
      verify();
      viewport.scrollIntoView({ block: 'center' });
      const grid = document.querySelector<HTMLElement>('[role="grid"]')!;
      grid.focus();
      grid.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
      await tick();
      assert(
        grid.getAttribute('aria-activedescendant') === 'script-' + host.snapshot!.scripts[0]!.id,
        'Keyboard selection lost',
      );
      assert(
        viewport.getBoundingClientRect().top < workspace.getBoundingClientRect().bottom,
        'List cannot scroll into reach',
      );
      if (innerWidth <= 900) {
        button(text.hideDetails, inspector).click();
        await tick();
        assert(inspector.hidden, 'Details did not close');
        verify();
        button(text.details).click();
        await tick();
        assert(!inspector.hidden, 'Details did not reopen');
      }
      noticeDetails.open = false;
      analysis.open = false;
      metadata.open = false;
      workspace.scrollTop = 0;
      await tick();
      verify();
      await window.fixtureHarness.resetResponses();
    },
  );
  await check(
    'semantic preference table preserves null, selection and native checkbox keyboard focus',
    async () => {
      button(text.scripts).click();
      const search = document.querySelector<HTMLInputElement>('input[aria-label="Search scripts"]');
      if (search) edit(search, '');
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState({
        ...host,
        snapshot: {
          ...host.snapshot!,
          scripts: [
            { ...host.snapshot!.scripts[0]!, runAtLogin: null },
            host.snapshot!.scripts[1]!,
          ],
        },
      });
      await tick();
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      await window.fixtureHarness.clearCalls();
      const table = document.querySelector<HTMLTableElement>('table[role="grid"]')!;
      assert(
        table.querySelectorAll('thead th').length === 5 &&
          table.querySelectorAll('tbody .script-row').length === 2,
        'Semantic five-column table missing',
      );
      assert(
        !table.querySelector('button input, [role="option"] input, [role="listbox"] input'),
        'Checkbox nested inside interactive selection control',
      );
      const first = document.getElementById('script-' + host.snapshot!.scripts[0]!.id)!;
      const login = first.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
      assert(
        !login.checked &&
          login.getAttribute('aria-label')?.includes(text.startupColumn) &&
          login.getAttribute('aria-label')?.includes(host.snapshot!.scripts[0]!.friendlyName),
        'Legacy null/accessibility label lost',
      );
      assert(
        (document.getElementById('draft-login') as HTMLInputElement).type === 'checkbox' &&
          !(document.getElementById('draft-login') as HTMLInputElement).checked,
        'Inspector startup is not unchecked nullable checkbox',
      );
      const second = document.getElementById('script-' + host.snapshot!.scripts[1]!.id)!;
      const checkbox = second.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
      checkbox.focus();
      checkbox.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
      await tick();
      assert(
        first.getAttribute('aria-selected') === 'true' && document.activeElement === checkbox,
        'Checkbox arrows changed selection or stole focus',
      );
      assert(
        !(await window.fixtureHarness.calls()).some(
          (call) => call.method === 'saveMetadata' || call.method === 'operate',
        ),
        'Passive null display/focus dispatched an effect',
      );
    },
  );
  await check(
    'cross-row checkbox saves only snapshot preference and preserves selection and another row draft',
    async () => {
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      const metadata = document.querySelector<HTMLDetailsElement>('.metadata-expander')!;
      metadata.open = true;
      edit(document.getElementById('draft-name') as HTMLInputElement, 'Draft for another row');
      await tick();
      await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
      await window.fixtureHarness.clearCalls();
      const target = host.snapshot!.scripts[1]!;
      const row = document.getElementById('script-' + target.id)!;
      const checkbox = row.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
      checkbox.click();
      await until(() => checkbox.disabled, 'Preference did not become pending/disabled');
      await untilCall('saveMetadata');
      const calls = await window.fixtureHarness.calls();
      const write = calls.find((call) => call.method === 'saveMetadata');
      assert(write, 'Preference request absent');
      assert(
        write.args[0] === target.id &&
          (write.args[1] as { friendlyName: string; runAtLogin: boolean }).friendlyName ===
            target.friendlyName &&
          (write.args[1] as { runAtLogin: boolean }).runAtLogin === true &&
          write.args[2] === host.snapshot!.revision,
        'Cross-row payload used selected draft or wrong revision',
      );
      checkbox.click();
      assert(
        (await window.fixtureHarness.calls()).filter((call) => call.method === 'saveMetadata')
          .length === 1,
        'Pending gesture wrote twice',
      );
      await window.fixtureHarness.setState({
        ...host,
        snapshot: {
          ...host.snapshot!,
          revision: '9007199254742001',
          scripts: [host.snapshot!.scripts[0]!, { ...target, runAtLogin: true }],
        },
      });
      await window.fixtureHarness.resolveResponse('saveMetadata', {
        value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null },
      });
      await until(() => !checkbox.disabled, 'Preference did not settle');
      assert(
        checkbox.checked &&
          document
            .getElementById('script-' + host.snapshot!.scripts[0]!.id)
            ?.getAttribute('aria-selected') === 'true',
        'Cross-row checkbox changed selection',
      );
      assert(
        String((document.getElementById('draft-name') as HTMLInputElement).value) ===
          'Draft for another row',
        'Other row dirty draft lost',
      );
      assert(
        !(await window.fixtureHarness.calls()).some(
          (call) => call.method === 'operate' || call.method === 'saveSettings',
        ),
        'Preference dispatched process/UAC/global settings',
      );
      await window.fixtureHarness.resetResponses();
    },
  );
  await check(
    'confirmed row admin choice rebases dirty manual draft so its later Save cannot undo preference',
    async () => {
      const base = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          scripts: [
            { ...host.snapshot!.scripts[0]!, runAtLogin: null, runAsAdministrator: false },
            host.snapshot!.scripts[1]!,
          ],
        },
      };
      await window.fixtureHarness.setState(base);
      await tick();
      selectScript(base.snapshot.scripts[0]!.id);
      await tick();
      const panel = document.querySelector('.inspector')!;
      const discard = button(text.discard, panel);
      if (!discard.disabled) {
        discard.click();
        await tick();
      }
      (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true;
      edit(document.getElementById('draft-name') as HTMLInputElement, 'Nome manual novo');
      edit(
        document.getElementById('draft-description') as HTMLTextAreaElement,
        'Texto manual novo',
      );
      await tick();
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
      const row = document.getElementById('script-' + base.snapshot.scripts[0]!.id)!;
      const admin = row.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[1]!;
      admin.click();
      await until(() => admin.disabled, 'Admin preference not pending');
      await untilCall('saveMetadata');
      const request = (await window.fixtureHarness.calls()).find(
        (call) => call.method === 'saveMetadata',
      )!;
      assert(
        (request.args[1] as { runAtLogin: unknown }).runAtLogin === null &&
          (request.args[1] as { friendlyName: string }).friendlyName ===
            base.snapshot.scripts[0]!.friendlyName,
        'Admin-only row write normalized null or saved dirty name',
      );
      const persisted = {
        ...base,
        snapshot: {
          ...base.snapshot,
          revision: '9007199254742002',
          scripts: [
            { ...base.snapshot.scripts[0]!, runAsAdministrator: true },
            base.snapshot.scripts[1]!,
          ],
        },
      };
      await window.fixtureHarness.setState(persisted);
      await window.fixtureHarness.resolveResponse('saveMetadata', {
        value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null },
      });
      await until(() => !admin.disabled, 'Admin preference did not settle');
      assert(
        admin.checked &&
          String((document.getElementById('draft-name') as HTMLInputElement).value) ===
            'Nome manual novo' &&
          String((document.getElementById('draft-description') as HTMLTextAreaElement).value) ===
            'Texto manual novo',
        'Confirmed row choice replaced manual draft',
      );
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
      await window.fixtureHarness.clearCalls();
      button(text.save, panel).click();
      await until(() => button(text.save, panel).disabled, 'Manual Save not pending');
      await untilCall('saveMetadata');
      const saved = (await window.fixtureHarness.calls()).find(
        (call) => call.method === 'saveMetadata',
      )!;
      assert(
        (saved.args[1] as { runAsAdministrator: boolean; runAtLogin: unknown })
          .runAsAdministrator === true &&
          (saved.args[1] as { runAtLogin: unknown }).runAtLogin === null,
        'Old manual Save undid row choice',
      );
      await window.fixtureHarness.setState({
        ...persisted,
        snapshot: {
          ...persisted.snapshot,
          revision: '9007199254742003',
          scripts: [
            {
              ...persisted.snapshot.scripts[0]!,
              friendlyName: 'Nome manual novo',
              description: 'Texto manual novo',
            },
            persisted.snapshot.scripts[1]!,
          ],
        },
      });
      await window.fixtureHarness.resolveResponse('saveMetadata', {
        value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null },
      });
      await tick();
      await window.fixtureHarness.resetResponses();
    },
  );
  await check(
    'cancel/failure preserve preference and draft while Partial/Unknown read authority without retry',
    async () => {
      for (const result of ['CancelledBeforeDispatch', 'Failed', 'Partial', 'Unknown'] as const) {
        await window.fixtureHarness.resetResponses();
        await window.fixtureHarness.setState(host);
        await tick();
        selectScript(host.snapshot!.scripts[0]!.id);
        await tick();
        (document.querySelector('.metadata-expander') as HTMLDetailsElement).open = true;
        edit(document.getElementById('draft-name') as HTMLInputElement, 'Draft ' + result);
        await tick();
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
        const row = document.getElementById('script-' + host.snapshot!.scripts[0]!.id)!;
        const checkbox = row.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
        checkbox.click();
        await until(() => checkbox.disabled, 'Preference did not serialize');
        await untilCall('saveMetadata');
        if (result === 'Partial' || result === 'Unknown')
          await window.fixtureHarness.setState({
            ...host,
            snapshot: {
              ...host.snapshot!,
              revision: result === 'Partial' ? '9007199254742010' : '9007199254742011',
              scripts: [
                { ...host.snapshot!.scripts[0]!, runAtLogin: true },
                host.snapshot!.scripts[1]!,
              ],
            },
          });
        await window.fixtureHarness.resolveResponse('saveMetadata', {
          value: {
            outcome: result,
            code:
              result === 'CancelledBeforeDispatch'
                ? 'Cancelled'
                : result === 'Failed'
                  ? 'InvalidMetadata'
                  : 'PendingOperation',
            detail: 'FIXTURE',
            correlationId: null,
          },
        });
        await until(() => !checkbox.disabled, 'Preference did not settle');
        const calls = await window.fixtureHarness.calls();
        assert(
          calls.filter((call) => call.method === 'saveMetadata').length === 1 &&
            !calls.some((call) => call.method === 'operate'),
          'Preference result replayed or operated a script',
        );
        assert(
          checkbox.checked === (result === 'Partial' || result === 'Unknown') &&
            String((document.getElementById('draft-name') as HTMLInputElement).value) ===
              'Draft ' + result,
          'Outcome lost authoritative value or manual intent',
        );
        if (result === 'Partial' || result === 'Unknown')
          assert(
            calls.some((call) => call.method === 'refresh') &&
              calls.filter((call) => call.method === 'getState').length >= 2,
            'Uncertain result did not re-read authority',
          );
        if (result === 'CancelledBeforeDispatch')
          assert(
            document.querySelector('.notice')?.textContent?.includes(text.preferenceCancelled),
            'Cancellation was not announced',
          );
      }
      await window.fixtureHarness.resetResponses();
    },
  );
  for (const field of ['runAsAdministrator', 'runAtLogin'] as const)
    for (const original of field === 'runAtLogin' ? [false, true, null] : [false, true])
      for (const outcome of ['Exception', 'Partial', 'Unknown'] as const) {
        await check(
          `${field} ${String(original)} unchanged after ${outcome} preserves same-field inspector draft and dirty state`,
          async () => {
            const requested = original !== true;
            const base = {
              ...host,
              snapshot: {
                ...host.snapshot!,
                scripts: [
                  {
                    ...host.snapshot!.scripts[0]!,
                    runAtLogin: null,
                    runAsAdministrator: false,
                    [field]: original,
                  },
                  host.snapshot!.scripts[1]!,
                ],
              },
            };
            await window.fixtureHarness.resetResponses();
            await window.fixtureHarness.setState(base);
            await tick();
            selectScript(base.snapshot.scripts[0]!.id);
            await tick();
            const panel = document.querySelector('.inspector')!;
            const discard = button(text.discard, panel);
            if (!discard.disabled) {
              discard.click();
              await tick();
            }
            (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true;
            const draft =
              field === 'runAtLogin'
                ? (document.getElementById('draft-login') as HTMLInputElement)
                : panel.querySelectorAll<HTMLInputElement>(
                    '.metadata-form input[type="checkbox"]',
                  )[1]!;
            draft.click();
            await tick();
            assert(
              draft.checked === requested && !button(text.save, panel).disabled,
              'Same-field draft was not dirty before row attempt',
            );
            await window.fixtureHarness.clearCalls();
            await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
            const row = document.getElementById('script-' + base.snapshot.scripts[0]!.id)!;
            const checkbox =
              row.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[
                field === 'runAtLogin' ? 0 : 1
              ]!;
            checkbox.click();
            await untilCall('saveMetadata');
            await window.fixtureHarness.resolveResponse(
              'saveMetadata',
              outcome === 'Exception'
                ? { error: 'FIXTURE write rejected before persistence' }
                : {
                    value: {
                      outcome,
                      code: 'PendingOperation',
                      detail: 'FIXTURE no persistence',
                      correlationId: null,
                    },
                  },
            );
            await until(() => !checkbox.disabled, 'Unpersisted preference did not settle');
            assert(
              checkbox.checked === (original === true),
              'Row did not retain authoritative preference',
            );
            assert(
              draft.checked === requested,
              'Unpersisted row attempt overwrote same-field inspector draft',
            );
            assert(
              !button(text.save, panel).disabled &&
                window.fixtureHarness.readGuard()?.dirtyDrafts === true,
              'Unpersisted row attempt cleared dirty state',
            );
            const calls = await window.fixtureHarness.calls();
            assert(
              calls.filter((call) => call.method === 'saveMetadata').length === 1 &&
                !calls.some((call) => call.method === 'operate' || call.method === 'saveSettings'),
              'Unpersisted row attempt retried or dispatched another effect',
            );
            if (field === 'runAsAdministrator')
              assert(
                (
                  calls.find((call) => call.method === 'saveMetadata')!.args[1] as {
                    runAtLogin: unknown;
                  }
                ).runAtLogin === null &&
                  !(document.getElementById('draft-login') as HTMLInputElement).checked,
                'Admin attempt normalized nullable login',
              );
            button(text.discard, panel).click();
            await tick();
            await window.fixtureHarness.resetResponses();
          },
        );
      }
  for (const field of ['runAsAdministrator', 'runAtLogin'] as const)
    await check(
      `${field} exception after persistence reconciles row choice while preserving unrelated draft`,
      async () => {
        const base = {
          ...host,
          snapshot: {
            ...host.snapshot!,
            scripts: [
              { ...host.snapshot!.scripts[0]!, runAtLogin: null, runAsAdministrator: false },
              host.snapshot!.scripts[1]!,
            ],
          },
        };
        await window.fixtureHarness.resetResponses();
        await window.fixtureHarness.setState(base);
        await tick();
        selectScript(base.snapshot.scripts[0]!.id);
        await tick();
        const panel = document.querySelector('.inspector')!;
        const discard = button(text.discard, panel);
        if (!discard.disabled) {
          discard.click();
          await tick();
        }
        (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true;
        edit(
          document.getElementById('draft-name') as HTMLInputElement,
          'Keep name after exception',
        );
        await tick();
        await window.fixtureHarness.clearCalls();
        await window.fixtureHarness.setResponse('saveMetadata', { pending: true });
        const checkbox = document
          .getElementById('script-' + base.snapshot.scripts[0]!.id)!
          .querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[
          field === 'runAtLogin' ? 0 : 1
        ]!;
        checkbox.click();
        await untilCall('saveMetadata');
        await window.fixtureHarness.setState({
          ...base,
          snapshot: {
            ...base.snapshot,
            revision: '9007199254742090',
            scripts: [{ ...base.snapshot.scripts[0]!, [field]: true }, base.snapshot.scripts[1]!],
          },
        });
        await window.fixtureHarness.resolveResponse('saveMetadata', {
          error: 'FIXTURE response lost after persistence',
        });
        await until(() => !checkbox.disabled, 'Persisted exception did not settle');
        const draft =
          field === 'runAtLogin'
            ? (document.getElementById('draft-login') as HTMLInputElement)
            : panel.querySelectorAll<HTMLInputElement>('.metadata-form input[type="checkbox"]')[1]!;
        assert(
          checkbox.checked &&
            draft.checked &&
            String((document.getElementById('draft-name') as HTMLInputElement).value) ===
              'Keep name after exception' &&
            !button(text.save, panel).disabled,
          'Persisted exception lost row choice or unrelated draft',
        );
        button(text.discard, panel).click();
        await tick();
        await window.fixtureHarness.resetResponses();
      },
    );
  await check(
    'admin plus login remains a manual-consent policy warning and failed existing guard never enables global startup',
    async () => {
      const state = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          settings: { ...host.snapshot!.settings, launchManagerAtLogin: false },
          scripts: [
            { ...host.snapshot!.scripts[0]!, runAtLogin: true, runAsAdministrator: false },
            host.snapshot!.scripts[1]!,
          ],
        },
      };
      await window.fixtureHarness.setState(state);
      await tick();
      selectScript(state.snapshot.scripts[0]!.id);
      await tick();
      const panel = document.querySelector('.inspector')!;
      const discard = button(text.discard, panel);
      if (!discard.disabled) {
        discard.click();
        await tick();
      }
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('saveMetadata', {
        value: {
          outcome: 'Failed',
          code: 'StartupConsentRequired',
          detail: null,
          correlationId: null,
        },
      });
      const admin = document
        .getElementById('script-' + state.snapshot.scripts[0]!.id)!
        .querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[1]!;
      admin.click();
      await tick();
      assert(
        !admin.checked &&
          !(await window.fixtureHarness.calls()).some(
            (call) => call.method === 'saveSettings' || call.method === 'operate',
          ),
        'Admin checkbox bypassed existing startup guard',
      );
      await window.fixtureHarness.setState({
        ...state,
        snapshot: {
          ...state.snapshot,
          scripts: [
            { ...state.snapshot.scripts[0]!, runAsAdministrator: true },
            state.snapshot.scripts[1]!,
          ],
        },
      });
      await tick();
      (panel.querySelector('.metadata-expander') as HTMLDetailsElement).open = true;
      const reset = button(text.discard, panel);
      if (!reset.disabled) {
        reset.click();
        await tick();
      }
      assert(
        panel.textContent?.includes(text.adminLoginHint),
        'Combined admin/login policy warning missing',
      );
      await window.fixtureHarness.resetResponses();
    },
  );
  await check(
    'administration preflight reads dirty drafts and dialogs without discarding either',
    async () => {
      const host = state();
      await window.fixtureHarness.setState(host);
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      const discard = [...document.querySelectorAll<HTMLButtonElement>('button')].find(
        (node) => node.textContent?.trim() === text.discard,
      );
      if (discard && !discard.disabled) discard.click();
      await tick();
      assert(
        window.fixtureHarness.readGuard()?.dirtyDrafts === false,
        'Clean draft incorrectly blocks handoff',
      );
      edit(
        document.getElementById('draft-name') as HTMLInputElement,
        'Draft preserved before elevation',
      );
      await tick();
      assert(
        window.fixtureHarness.readGuard()?.dirtyDrafts === true,
        'Dirty draft omitted from preflight',
      );
      button(text.discard).click();
      await tick();
      await window.fixtureHarness.openPreferences();
      await tick();
      assert(
        window.fixtureHarness.readGuard()?.dialogOpen === true,
        'Open preference dialog omitted from preflight',
      );
      button(text.cancel, document.querySelector('[role="dialog"]')!).click();
      await tick();
    },
  );
  await check(
    'administration quiescence freezes all content inputs and sends no row save or operation',
    async () => {
      const host = state();
      await window.fixtureHarness.setState(host);
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      await window.fixtureHarness.clearCalls();
      const previous = (document.getElementById('draft-name') as HTMLInputElement).value;
      await window.fixtureHarness.setState({ ...host, administrationPending: true });
      await tick();
      assert(
        document.querySelector('.app-shell')?.hasAttribute('inert'),
        'Content remains interactive while authority is unknown',
      );
      edit(
        document.getElementById('draft-name') as HTMLInputElement,
        'Cannot change while quiescent',
      );
      const checkbox = document.querySelector<HTMLInputElement>(
        'tbody tr.script-row input[type="checkbox"]',
      );
      checkbox?.click();
      await tick();
      assert(
        (document.getElementById('draft-name') as HTMLInputElement).value === previous &&
          !window.fixtureHarness.readGuard()?.dirtyDrafts,
        'Quiescence changed draft',
      );
      assert(
        !(await window.fixtureHarness.calls()).some((call) =>
          ['saveMetadata', 'operate', 'saveSettings'].includes(call.method),
        ),
        'Quiescence dispatched an effect',
      );
      assert(
        button(text.checkAdministration) &&
          document.querySelector('.authority-badge')?.textContent === text.authorityNormal,
        'Unknown transfer has no explicit status recovery',
      );
      await window.fixtureHarness.setState(host);
      await tick();
    },
  );
  await check(
    'authenticated host badge and per-process capability barriers are independent of preference',
    async () => {
      const host = state();
      host.authorityMode = 'Administrator';
      host.snapshot!.normalLaunchUnavailableReason = 'CapabilityUnverified';
      await window.fixtureHarness.setState(host);
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      assert(
        document.querySelector('.authority-badge')?.textContent === text.authorityAdministrator,
        'High mode not shown from host proof',
      );
      assert(
        button(operation('Start'), document.querySelector('.inspector')!).disabled,
        'Normal launch capability barrier ignored',
      );
      assert(
        !(document.querySelector('.inspector')?.textContent ?? '').includes(text.adminRequested),
        'Preference false falsely reported admin request',
      );
      await window.fixtureHarness.setState({ ...host, authorityMode: null });
      await tick();
      assert(
        document.querySelector('.authority-badge')?.textContent === text.authorityUnverified,
        'Unknown host reported authenticated mode',
      );
    },
  );
  await check(
    'root header is escaped multiline primary text; refresh/removal preserve editable manual fallback draft',
    async () => {
      button(text.scripts).click();
      const search = document.querySelector<HTMLInputElement>('input[aria-label="Search scripts"]');
      if (search) edit(search, '');
      const header =
        '<script>literal without execution</script>\nSecond line: ' + 'word'.repeat(110);
      const withHeader: HostState = {
        ...host,
        snapshot: {
          ...host.snapshot!,
          scripts: [
            {
              ...host.snapshot!.scripts[0]!,
              description: 'Saved manual description',
              headerDescription: header,
            },
            host.snapshot!.scripts[1]!,
          ],
        },
      };
      await window.fixtureHarness.setState(withHeader);
      await tick();
      selectScript(withHeader.snapshot!.scripts[0]!.id);
      await tick();
      await until(
        () => !!document.getElementById('draft-description'),
        'Description editor missing',
      );
      const panel = document.querySelector<HTMLElement>('.inspector')!;
      const discard = button(text.discard, panel);
      if (!discard.disabled) {
        discard.click();
        await tick();
      }
      const metadata = panel.querySelector<HTMLDetailsElement>('.metadata-expander')!;
      metadata.open = true;
      const manual = document.getElementById('draft-description') as HTMLTextAreaElement;
      assert(
        String(manual.value) === 'Saved manual description',
        'Header overwrote manual metadata',
      );
      assert(
        panel.querySelector('.script-description')?.children.length === 1 &&
          !panel.querySelector('.description-guidance') &&
          !panel.querySelector('.script-description h3'),
        'Present header must show only its description',
      );
      edit(manual, 'Manual description being edited');
      await tick();
      await window.fixtureHarness.clearCalls();
      for (const dark of [false, true]) {
        await window.fixtureHarness.setTheme({ ...light, dark });
        await tick();
        const paragraph = panel.querySelector<HTMLElement>('.description')!;
        assert(
          paragraph.textContent === header && !paragraph.querySelector('script,img'),
          'Header HTML interpreted instead of escaped',
        );
        assert(
          getComputedStyle(paragraph).whiteSpace === 'pre-wrap' &&
            getComputedStyle(paragraph).overflowWrap === 'anywhere',
          'Header multiline/wrapping lost',
        );
        assert(
          paragraph.scrollWidth <= paragraph.clientWidth + 1 &&
            paragraph.scrollHeight <= paragraph.clientHeight + 1,
          'Header text clipped',
        );
      }
      await window.fixtureHarness.setState({
        ...withHeader,
        snapshot: {
          ...withHeader.snapshot!,
          revision: '9007199254741800',
          scripts: [
            { ...withHeader.snapshot!.scripts[0]!, headerDescription: 'Updated header' },
            withHeader.snapshot!.scripts[1]!,
          ],
        },
      });
      await tick();
      assert(
        manual.value === 'Manual description being edited' &&
          panel.querySelector('.description')?.textContent === 'Updated header',
        'External header refresh replaced dirty manual draft',
      );
      await window.fixtureHarness.setState({
        ...withHeader,
        snapshot: {
          ...withHeader.snapshot!,
          scripts: [
            { ...withHeader.snapshot!.scripts[0]!, headerDescription: null },
            withHeader.snapshot!.scripts[1]!,
          ],
        },
      });
      await tick();
      assert(
        panel.querySelector('.description')?.textContent === 'Saved manual description' &&
          manual.value === 'Manual description being edited',
        'Header removal did not restore snapshot manual fallback independently',
      );
      assert(
        panel
          .querySelector('.description-guidance')
          ?.textContent?.includes(text.addDescriptionHint) &&
          panel.querySelector('.description-guidance pre')?.textContent ===
            text.descriptionHeaderExample,
        'Absent header lacks localized syntax guidance',
      );
      assert(
        panel.querySelector('.description-guidance')?.closest('.metadata-expander') === metadata &&
          !(panel.querySelector('.description-guidance') as HTMLDetailsElement).open,
        'Header guidance must be collapsed inside editable information',
      );
      await window.fixtureHarness.setState({
        ...withHeader,
        snapshot: {
          ...withHeader.snapshot!,
          scripts: [
            { ...withHeader.snapshot!.scripts[0]!, description: '', headerDescription: null },
            withHeader.snapshot!.scripts[1]!,
          ],
        },
      });
      await tick();
      assert(
        !panel.querySelector('.description') &&
          panel
            .querySelector('.description-guidance')
            ?.textContent?.includes(text.descriptionRefreshHint) &&
          manual.value === 'Manual description being edited',
        'Completely empty description must show guidance alone without replacing draft',
      );
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'saveMetadata'),
        'Derived refresh persisted without explicit save',
      );
      await window.fixtureHarness.setState(withHeader);
      await tick();
      metadata.open = false;
      panel.scrollTop = 0;
    },
  );
  results.push(...(await runUpdateScenarios()));
  return results;
}

// A focused isolated preview lets the same harness capture the absent-header state.
export async function previewDescription(): Promise<AssertionRecord[]> {
  const host = state();
  host.snapshot!.scripts[0] = {
    ...host.snapshot!.scripts[0]!,
    description: 'Manual description preserved.',
    headerDescription: null,
  };
  await window.fixtureHarness.setState(host);
  await window.fixtureHarness.setTheme({ ...light, dark: true });
  selectScript(host.snapshot!.scripts[0]!.id);
  await tick();
  const panel = document.querySelector('.inspector')!;
  assert(
    panel.querySelector('.description')?.textContent === 'Manual description preserved.' &&
      panel.querySelector('.description-guidance pre')?.textContent ===
        text.descriptionHeaderExample,
    'Absent-header preview did not preserve manual/guidance',
  );
  return [
    {
      name: 'absent-header focused preview preserves manual and exact comment example',
      passed: true,
    },
  ];
}

export async function previewHotkeys(missing = false): Promise<AssertionRecord[]> {
  button(text.scripts).click();
  const host = state();
  host.snapshot!.scripts[0] = {
    ...host.snapshot!.scripts[0]!,
    friendlyName: 'Example hotkeys',
    path: 'C:\\ISOLATED FIXTURE\\Long path for testing text wrapping\\Example project\\test file.ahk',
    description: 'Synthetic fixture. No real script is executed.',
    status: missing ? 'Missing' : 'Stopped',
    issues: missing ? [{ code: 'MissingFile', detail: null, scriptId: null }] : [],
    hotkeys: [
      {
        chord: '!^#+u',
        normalizedChord: '!^#+u',
        origin: 'Parsed',
        context: 'Example context',
        sourcePath: null,
        line: 1,
        isRemap: false,
        possibleConflict: true,
      },
      {
        chord: '~<^>!LButton Up',
        normalizedChord: '~<^>!LButton Up',
        origin: 'Parsed',
        context: null,
        sourcePath: null,
        line: 3,
        isRemap: false,
        possibleConflict: false,
        declaredGesture: 'DoubleClick',
      },
      {
        chord: 'F13',
        normalizedChord: 'F13',
        origin: 'Parsed',
        context: null,
        sourcePath: null,
        line: 5,
        isRemap: false,
        possibleConflict: false,
      },
    ],
  };
  await window.fixtureHarness.setState(host);
  await tick();
  selectScript(host.snapshot!.scripts[0]!.id);
  await tick();
  const discard = [...document.querySelectorAll<HTMLButtonElement>('button')].find(
    (node) => node.textContent?.trim() === text.discard,
  );
  if (discard && !discard.disabled) {
    discard.click();
    await tick();
  }
  document.querySelector<HTMLButtonElement>('.notice button')?.click();
  await tick();
  const panel = document.querySelector<HTMLElement>('.inspector')!;
  panel.scrollTop = missing
    ? 0
    : Math.max(0, (panel.querySelector('.hotkey-details') as HTMLElement)?.offsetTop - 180);
  if (innerWidth <= 900) panel.scrollIntoView({ block: 'start' });
  return [
    {
      name: 'synthetic hotkeys and long path preview',
      passed:
        !document.documentElement.scrollWidth || document.documentElement.scrollWidth <= innerWidth,
    },
  ];
}

// Keep the selected script's primary actions visible for CI visual review.
export async function previewOverview(): Promise<AssertionRecord[]> {
  const results = await previewHotkeys(false);
  const panel = document.querySelector<HTMLElement>('.inspector')!;
  const metadata = panel.querySelector<HTMLDetailsElement>('.metadata-expander');
  if (metadata) metadata.open = false;
  panel.scrollTop = 0;
  const workspace = document.querySelector<HTMLElement>('.workspace');
  if (workspace) workspace.scrollTop = 0;
  await tick();
  return results;
}

export async function focusRecovery(): Promise<AssertionRecord[]> {
  await previewHotkeys(true);
  for (const label of [text.locateScript, text.remove]) {
    const node = button(label, document.querySelector('.missing-file-notice')!);
    node.focus({ preventScroll: true });
    node.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    await tick();
    const rect = node.getBoundingClientRect();
    assert(
      document.activeElement === node &&
        !node.disabled &&
        rect.width > 0 &&
        rect.left >= 0 &&
        rect.right <= innerWidth &&
        rect.top >= 0 &&
        rect.bottom <= innerHeight,
      'Recovery action unreachable at zoom: ' + label,
    );
  }
  document
    .querySelector('.missing-file-notice .file-actions')!
    .scrollIntoView({ block: 'center', behavior: 'instant' });
  await tick();
  for (const node of document.querySelectorAll<HTMLButtonElement>(
    '.missing-file-notice .file-actions button',
  )) {
    const rect = node.getBoundingClientRect();
    assert(
      rect.top >= 0 && rect.bottom <= innerHeight,
      'Recovery buttons do not fit together after scrolling',
    );
  }
  return [
    {
      name: 'Localizar and Remover individually keyboard focused, wrapped and visible at200% zoom',
      passed: true,
    },
  ];
}

export async function checkPrimaryLabel(): Promise<AssertionRecord[]> {
  const key = document.querySelector<HTMLElement>('.row-hotkey kbd')!;
  const range = document.createRange();
  range.selectNodeContents(key);
  const bounds = key.getBoundingClientRect(),
    lines = [...range.getClientRects()];
  assert(
    key.textContent === 'Ctrl + Alt + Shift + Win + U' &&
      lines.length <= 2 &&
      lines.every((line) => line.bottom <= bounds.bottom + 1),
    'Complete primary shortcut does not fit two visible lines at normal1240',
  );
  return [
    {
      name: '1240px primary shortcut visibly retains all modifiers and U within two lines',
      passed: true,
    },
  ];
}

export async function runDeleteSelectionScopes(): Promise<AssertionRecord[]> {
  const results: AssertionRecord[] = [];
  const filter = (value: string) => {
    const node = document.querySelector<HTMLSelectElement>('.script-filters select')!;
    node.value = value;
    node.dispatchEvent(new Event('change', { bubbles: true }));
  };
  for (const scope of ['Running', 'Stopped'] as const) {
    let waiting = false;
    const host = state();
    host.snapshot!.scripts[0]!.status = scope;
    try {
      button(text.scripts).click();
      await window.fixtureHarness.resetResponses();
      await window.fixtureHarness.setState(host);
      await tick();
      edit(input('Search scripts'), '');
      filter(scope);
      await tick();
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      const discard = [...document.querySelectorAll<HTMLButtonElement>('button')].find(
        (node) => node.textContent?.trim() === text.discard,
      );
      if (discard && !discard.disabled) {
        discard.click();
        await tick();
      }
      await window.fixtureHarness.setResponse('deleteScriptFile', { pending: true });
      await window.fixtureHarness.clearCalls();
      button(text.deleteFile, document.querySelector('.inspector')!).click();
      await untilCall('deleteScriptFile');
      waiting = true;
      const pending = {
        ...host,
        busy: true,
        snapshot: {
          ...host.snapshot!,
          revision: '9007199254741100',
          scripts: [
            {
              ...host.snapshot!.scripts[0]!,
              status: 'Unknown' as const,
              process: null,
              issues: [
                { code: 'PendingOperation', detail: null, scriptId: host.snapshot!.scripts[0]!.id },
              ],
            },
            host.snapshot!.scripts[1]!,
          ],
        },
      };
      await window.fixtureHarness.setState(pending);
      await tick();
      assert(
        document.querySelector('.inspector-name')?.textContent ===
          host.snapshot!.scripts[0]!.friendlyName,
        'Own Pending hid active delete Inspector under ' + scope,
      );
      assert(
        !(await window.fixtureHarness.calls()).some(
          (call) => call.method === 'updateSelection' && call.args[0] === null,
        ),
        'Own Pending invalidated controller epoch under ' + scope,
      );
      await window.fixtureHarness.clearCalls();
      if (scope === 'Running') {
        edit(input('Search scripts'), 'no matching scope');
        await tick();
        assert(
          (await window.fixtureHarness.calls()).some(
            (call) => call.method === 'updateSelection' && call.args[0] === null,
          ),
          'Real query change did not invalidate delete scope',
        );
      } else {
        selectScript(host.snapshot!.scripts[1]!.id);
        await tick();
        assert(
          (await window.fixtureHarness.calls()).some(
            (call) =>
              call.method === 'updateSelection' &&
              (call.args[0] as { scriptId?: string } | null)?.scriptId ===
                host.snapshot!.scripts[1]!.id,
          ),
          'Real selection change did not reach controller',
        );
      }
      await window.fixtureHarness.resolveResponse('deleteScriptFile', {
        value: {
          outcome: 'Partial',
          code: 'Cancelled',
          detail: 'Execution ended.',
          correlationId: null,
        },
      });
      waiting = false;
      await window.fixtureHarness.setState({ ...pending, busy: false });
      await tick();
      if (scope === 'Stopped')
        assert(
          document.querySelector('.inspector-name')?.textContent ===
            host.snapshot!.scripts[1]!.friendlyName,
          'Old delete completion cleared new selection',
        );
      results.push({
        name:
          'delete own Pending retains ' +
          scope +
          ' scope; real user change and late cleanup preserve cancellation',
        passed: true,
      });
    } catch (error) {
      results.push({
        name: 'delete own Pending ' + scope + ' scope',
        passed: false,
        detail: String(error),
      });
    } finally {
      if (waiting)
        await window.fixtureHarness.resolveResponse('deleteScriptFile', {
          value: {
            outcome: 'Partial',
            code: 'Cancelled',
            detail: 'fixture cleanup',
            correlationId: null,
          },
        });
      await window.fixtureHarness.setState(host);
      await tick();
      filter('All');
      edit(input('Search scripts'), '');
      await tick();
    }
  }
  return results;
}

export async function runStartInTrayScenario(): Promise<AssertionRecord[]> {
  const host = state();
  try {
    const initial = {
      ...host,
      snapshot: {
        ...host.snapshot!,
        settings: { ...host.snapshot!.settings, showWindowAtLogin: true },
      },
    };
    await window.fixtureHarness.setState(initial);
    await tick();
    await window.fixtureHarness.clearCalls();
    const openPreferences = async () => {
      button(text.preferences).click();
      await until(
        () => !!document.getElementById('settings-start-in-tray'),
        'Tray preference missing',
      );
      await tick();
    };
    const tray = () => document.getElementById('settings-start-in-tray') as HTMLInputElement;
    const login = () => document.getElementById('settings-show-at-login') as HTMLInputElement;
    await openPreferences();
    assert(!tray().checked, 'Tray startup default changed');
    assert(
      document.querySelector('label[for="settings-start-in-tray"]')?.textContent ===
        'Start in tray',
      'Checkbox label differs',
    );
    tray().click();
    await tick();
    assert(
      tray().checked && login().checked && login().disabled,
      'Login visibility precedence or retained value missing',
    );
    assert(
      !(await window.fixtureHarness.calls()).some((call) => call.method === 'saveSettings'),
      'Draft applied before save',
    );
    button(text.cancel, document.querySelector('[role="dialog"]')!).click();
    await tick();
    await openPreferences();
    assert(!tray().checked && !login().disabled, 'Cancel changed preference');
    tray().click();
    await tick();
    await window.fixtureHarness.setResponse('saveSettings', {
      value: {
        outcome: 'Failed',
        code: 'StorageFailure',
        detail: 'FIXTURE save failure',
        correlationId: null,
      },
    });
    button(text.save, document.querySelector('[role="dialog"]')!).click();
    await until(() => !!document.querySelector('[role="dialog"] .notice'), 'Save failure absent');
    assert(tray().checked && login().checked && login().disabled, 'Save failure lost draft');
    const calls = (await window.fixtureHarness.calls()).filter(
      (call) => call.method === 'saveSettings',
    );
    const saved = calls[0]!.args[0] as ManagerSnapshot['settings'];
    assert(
      saved.startInTray && !saved.launchManagerAtLogin && saved.showWindowAtLogin,
      'Save altered independent startup settings',
    );
    await window.fixtureHarness.setResponse('saveSettings', {
      value: { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null },
    });
    button(text.save, document.querySelector('[role="dialog"]')!).click();
    await until(() => !document.querySelector('[role="dialog"]'), 'Confirmed save did not close');
    await window.fixtureHarness.setState({
      ...initial,
      snapshot: { ...initial.snapshot, settings: saved },
    });
    await tick();
    await openPreferences();
    assert(tray().checked, 'Reopened saved preference differs');
    tray().click();
    await tick();
    assert(
      !login().disabled && login().checked,
      'Disabling tray startup discarded login preference',
    );
    button(text.cancel, document.querySelector('[role="dialog"]')!).click();
    await tick();
    await window.fixtureHarness.resetResponses();
    await window.fixtureHarness.setState(host);
    await tick();
    return [
      {
        name: 'start in tray checkbox preserves cancel, failure, save and login preferences',
        passed: true,
      },
    ];
  } catch (error) {
    return [
      {
        name: 'start in tray checkbox preserves cancel, failure, save and login preferences',
        passed: false,
        detail: String(error),
      },
    ];
  }
}

export async function previewStartupRepair(): Promise<AssertionRecord[]> {
  const previous = document.querySelector('[role="dialog"]');
  if (previous) {
    const close = [...previous.querySelectorAll<HTMLButtonElement>('button')].find((node) =>
      [text.close, text.cancel].some((label) => label === node.textContent?.trim()),
    );
    assert(close, 'Previous dialog cannot be closed');
    close.click();
    await tick();
  }
  await window.fixtureHarness.resetResponses();
  await window.fixtureHarness.setResponse('getStorageState', { value: startupRepairStorage });
  const host = state();
  await window.fixtureHarness.setState({
    ...host,
    authorityMode: 'Administrator',
    snapshot: {
      ...host.snapshot!,
      settings: {
        ...host.snapshot!.settings,
        launchManagerAtLogin: true,
        launchManagerAsAdministrator: true,
        startInTray: true,
      },
    },
  });
  const preview = {
    ...startupRepairFixture(),
    detail: 'Manager will start with Windows as administrator. The current task will be preserved.',
  };
  await window.fixtureHarness.setResponse('previewStartupRepair', { value: preview });
  await window.fixtureHarness.clearCalls();
  await tick();
  button(text.preferences).click();
  await until(
    () => !!document.querySelector('.startup-entry-review .startup-previous-executable'),
    'Startup entry review preview missing',
  );
  const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;
  const review = dialog.querySelector<HTMLElement>('.startup-entry-review')!;
  review.scrollIntoView({ block: 'center', behavior: 'instant' });
  await tick();
  const action = button(text.removePreviousStartup, review);
  assert(
    review.querySelector('.startup-previous-executable')?.textContent ===
      preview.previousExecutable &&
      review.querySelector('.startup-current-executable')?.textContent ===
        preview.currentExecutable &&
      review.textContent?.includes(preview.detail) &&
      !action.disabled &&
      dialog.scrollWidth <= dialog.clientWidth + 1 &&
      document.documentElement.scrollWidth <= innerWidth &&
      !(await window.fixtureHarness.calls()).some((call) => call.method === 'repairStartup'),
    'Startup review omitted its evidence, overflowed or dispatched without explicit consent',
  );
  return [
    {
      name: 'startup review displays both verified paths and saved-mode consent without effects',
      passed: true,
    },
  ];
}

export async function previewHelp(): Promise<AssertionRecord[]> {
  const previous = document.querySelector('[role="dialog"]');
  if (previous) {
    const close = [...previous.querySelectorAll<HTMLButtonElement>('button')].find((node) =>
      [text.close, text.cancel].some((label) => label === node.textContent?.trim()),
    );
    assert(close, 'Previous dialog cannot be closed');
    close.click();
    await tick();
  }
  await window.fixtureHarness.resetResponses();
  await window.fixtureHarness.setState(state());
  await tick();
  button(text.help).click();
  await until(() => !!document.querySelector('[role="dialog"]'), 'Help preview missing');
  const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;
  const actions = dialog.querySelector<HTMLElement>('.diagnostic-actions')!;
  assert(actions, 'Diagnostic actions missing');
  actions.scrollIntoView({ block: 'center', behavior: 'instant' });
  await tick();
  const nodes = [
    text.saveDiagnosticArchive,
    text.copyDiagnosticReport,
    text.copyRecentErrors,
    text.openLogs,
  ].map((label) => button(label, dialog));
  for (const node of nodes) {
    const rect = node.getBoundingClientRect();
    assert(
      !node.disabled &&
        rect.width > 0 &&
        rect.left >= 0 &&
        rect.right <= innerWidth &&
        rect.top >= 0 &&
        rect.bottom <= innerHeight,
      'Diagnostic action clipped or unavailable: ' + node.textContent,
    );
  }
  assert(actions.scrollWidth <= actions.clientWidth, 'Diagnostic buttons overflow their group');
  return [
    {
      name: 'Help shows complete archive, both summaries and logs folder with visible wrapping actions',
      passed: true,
    },
  ];
}

export async function previewPreferences(): Promise<AssertionRecord[]> {
  const previous = document.querySelector('[role="dialog"]');
  if (previous) button(text.cancel, previous).click();
  await window.fixtureHarness.resetResponses();
  const host = state();
  host.snapshot!.settings = {
    ...host.snapshot!.settings,
    launchManagerAtLogin: true,
    launchManagerAsAdministrator: true,
  };
  await window.fixtureHarness.setState(host);
  await tick();
  await window.fixtureHarness.openPreferences();
  await tick();
  const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;
  assert(
    dialog.querySelector('.preference-section h3')?.textContent === text.startup,
    'Startup preferences not first',
  );
  assert(
    !!document.getElementById('settings-administrator-policy') &&
      dialog.textContent?.includes(text.managerAdministratorRequired) === true &&
      !document.getElementById('settings-administrator'),
    'Mandatory administrator policy absent or still configurable',
  );
  const content = dialog.querySelector<HTMLElement>('.fui-DialogContent');
  if (content) content.scrollTop = 0;
  return [
    {
      name: 'preferences preview shows startup defaults and automatic VS Code editor',
      passed: true,
    },
  ];
}

export async function previewDataLocation(): Promise<AssertionRecord[]> {
  const previous = document.querySelector('[role="dialog"]');
  if (previous) button(text.cancel, previous).click();
  await window.fixtureHarness.resetResponses();
  await window.fixtureHarness.setState(state());
  await tick();
  await window.fixtureHarness.setResponse('getStorageState', {
    value: {
      mode: 'portable',
      currentDataDirectory: 'C:\\Portable Manager\\Data',
      defaultDataDirectory: 'C:\\Portable Manager\\Data',
      pendingDataDirectory: 'D:\\Personal files\\AutoHotkey Manager\\Data',
      canChange: false,
      error: null,
    },
  });
  await window.fixtureHarness.openPreferences();
  await until(
    () =>
      !!document
        .querySelector('[aria-labelledby="settings-data-location-title"] [role="status"]')
        ?.textContent?.includes('Prepared data location'),
    'Prepared data preview did not load',
  );
  document
    .querySelector('[aria-labelledby="settings-data-location-title"]')
    ?.scrollIntoView({ block: 'center' });
  await tick();
  return [
    {
      name: 'portable data preview shows active and prepared locations with explicit restart and cancel',
      passed: true,
    },
  ];
}

function updateFixture(): UpdateState {
  return {
    channel: 'stable',
    currentVersion: '1.2.1',
    phase: 'idle',
    availableVersion: '1.2.2',
    lastCheckedAt: '2026-10-28T11:30:00Z',
    error: null,
    progress: null,
    downloadId: null,
    downloadCancellation: null,
    releases: [
      {
        id: 'fixture-new-122',
        version: '1.2.2',
        publishedAt: '2026-10-28T10:00:00Z',
        notes: 'Improves script startup and preference organization.\nExample fixture notes.',
        commit: 'a'.repeat(40),
        installed: false,
        canInstall: true,
        cached: false,
      },
      {
        id: 'fixture-installed-121',
        version: '1.2.1',
        publishedAt: '2026-10-25T10:00:00Z',
        notes: 'Installed version in this fixture.',
        commit: 'b'.repeat(40),
        installed: true,
        canInstall: false,
        cached: true,
      },
      {
        id: 'fixture-cached-120',
        version: '1.2.0',
        publishedAt: '2026-10-19T10:00:00Z',
        notes: 'Earlier version available on this computer. <img src=x onerror=alert(1)>',
        commit: 'c'.repeat(40),
        installed: false,
        canInstall: true,
        cached: true,
      },
    ],
  };
}

function testUpdateFixture(): UpdateState {
  const updates = updateFixture();
  const version = '1.3.0-test.18.1.2';
  return {
    ...updates,
    channel: 'test',
    availableVersion: version,
    releases: [
      {
        ...updates.releases[0]!,
        id: 'fixture-test-130',
        version,
        notes:
          'Preview of application organization and update channels. Available to evaluate before the stable release.',
      },
      ...updates.releases,
    ],
  };
}

export async function runUpdateScenarios(): Promise<AssertionRecord[]> {
  const results: AssertionRecord[] = [];
  const host = state(),
    updates = updateFixture();
  const outstanding = new Set<string>();
  async function check(name: string, test: () => Promise<void>): Promise<void> {
    try {
      await test();
      results.push({ name, passed: true });
    } catch (error) {
      results.push({ name, passed: false, detail: String(error) });
    } finally {
      for (const method of outstanding) {
        try {
          await window.fixtureHarness.resolveResponse(method, { value: updates });
        } catch {
          /* A completed fake request needs no release. */
        }
      }
      outstanding.clear();
      await tick();
      await window.fixtureHarness.resetResponses();
      const modal = document.querySelector('[role="dialog"]');
      if (modal) button(text.cancel, modal).click();
      await window.fixtureHarness.setState(host);
      await window.fixtureHarness.setUpdates(updates);
      await tick();
      button(text.scripts).click();
      await tick();
      for (const row of host.snapshot!.scripts) {
        selectScript(row.id);
        await tick();
        const discard = document.querySelector<HTMLButtonElement>(
          '.inspector .form-actions button:last-child',
        );
        if (discard && !discard.disabled) discard.click();
        await tick();
      }
    }
  }
  await check(
    'changing channel dispatches once, disables conflicting actions and never installs automatically',
    async () => {
      await window.fixtureHarness.setState(host);
      await window.fixtureHarness.setUpdates(updates);
      await tick();
      button(text.updates).click();
      await tick();
      const selector = document.getElementById('update-channel') as HTMLSelectElement;
      assert(
        selector.value === 'stable' && !selector.disabled,
        'Stable channel is not the default',
      );
      assert(
        selector.labels?.[0]?.textContent === text.updateChannel,
        'Channel label is not associated',
      );
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('setUpdateChannel', { pending: true });
      outstanding.add('setUpdateChannel');
      selector.value = 'test';
      selector.dispatchEvent(new Event('change', { bubbles: true }));
      selector.dispatchEvent(new Event('change', { bubbles: true }));
      await untilCall('setUpdateChannel');
      await tick();
      assert(
        selector.disabled && button(text.updateInstall).disabled,
        'Pending channel switch allowed another update action',
      );
      const calls = await window.fixtureHarness.calls();
      assert(
        calls.filter((call) => call.method === 'setUpdateChannel').length === 1,
        'Channel change was repeated',
      );
      assert(
        calls.find((call) => call.method === 'setUpdateChannel')?.args[0] === 'test',
        'Channel request differs from selection',
      );
      await window.fixtureHarness.resolveResponse('setUpdateChannel', {
        value: testUpdateFixture(),
      });
      outstanding.delete('setUpdateChannel');
      await tick();
      assert(
        selector.value === 'test' && !selector.disabled,
        'Selected test channel is not reflected',
      );
      assert(
        document.querySelector('.update-release-badges')?.textContent?.includes(text.updatePreview),
        'Test release lacks a visible preview label',
      );
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'installUpdate'),
        'Changing channel installed a version automatically',
      );
    },
  );
  await check(
    'returning to stable offers an explicit rollback from an installed test version',
    async () => {
      const preview = testUpdateFixture();
      preview.currentVersion = preview.availableVersion!;
      preview.availableVersion = null;
      preview.releases = preview.releases.map((release) => ({
        ...release,
        installed: release.version === preview.currentVersion,
        canInstall: release.version !== preview.currentVersion,
      }));
      await window.fixtureHarness.setUpdates(preview);
      await tick();
      button(text.updates).click();
      await tick();
      const stable = { ...updates, currentVersion: preview.currentVersion };
      await window.fixtureHarness.setResponse('setUpdateChannel', { value: stable });
      await window.fixtureHarness.clearCalls();
      const selector = document.getElementById('update-channel') as HTMLSelectElement;
      selector.value = 'stable';
      selector.dispatchEvent(new Event('change', { bubbles: true }));
      await untilCall('setUpdateChannel');
      await tick();
      assert(
        document.querySelector('.updates-availability')?.textContent ===
          `${text.updateStableAvailable} ${stable.availableVersion}`,
        'Stable version is described as a newer preview',
      );
      const rollback = button(
        text.updateRollback,
        document.querySelector('.updates-summary-actions')!,
      );
      assert(!rollback.disabled && selector.value === 'stable', 'Stable rollback is not available');
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'installUpdate'),
        'Returning to stable installed without an explicit action',
      );
      await window.fixtureHarness.setResponse('installUpdate', { value: stable });
      rollback.click();
      await untilCall('installUpdate');
      assert(
        (await window.fixtureHarness.calls()).find((call) => call.method === 'installUpdate')
          ?.args[0] === 'fixture-new-122',
        'Stable rollback selected the wrong release',
      );
    },
  );
  await check(
    'failed channel persistence retains the previous selection and shows an error',
    async () => {
      await window.fixtureHarness.setUpdates(updates);
      await tick();
      button(text.updates).click();
      await tick();
      await window.fixtureHarness.setResponse('setUpdateChannel', {
        error: 'Fixture persistence failure',
      });
      const selector = document.getElementById('update-channel') as HTMLSelectElement;
      selector.value = 'test';
      selector.dispatchEvent(new Event('change', { bubbles: true }));
      await until(
        () =>
          selector.value === 'stable' &&
          !selector.disabled &&
          document.querySelector('[role="alert"]')?.textContent === text.updateChannelFailed,
        'Failed channel change did not restore the previous choice and announce the error',
      );
      assert(
        document.querySelector('[role="alert"]')?.textContent === text.updateChannelFailed,
        'Channel failure is not announced',
      );
    },
  );
  await check(
    'update navigation announces availability and check keeps escaped permanent history on error',
    async () => {
      await window.fixtureHarness.setState(host);
      await window.fixtureHarness.setUpdates(updates);
      await tick();
      assert(
        document.querySelector('.update-nav-dot') && button(text.updateOpen),
        'New version indicator absent',
      );
      button(text.updateOpen).click();
      await tick();
      assert(
        document.querySelector('h1')?.textContent === text.updates && button(text.updateInstall),
        'Update page or primary action absent',
      );
      assert(
        document.querySelectorAll('.update-release').length === 3 &&
          !document.querySelector('.update-release-notes img'),
        'History is incomplete or interpreted release markup',
      );
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('checkUpdates', {
        value: {
          ...updates,
          phase: 'error',
          error: 'Offline. Local versions were preserved.',
        },
      });
      button(text.updateCheck).click();
      await untilCall('checkUpdates');
      await tick();
      assert(
        document.querySelectorAll('.update-release').length === 3 &&
          document.querySelector('.update-feedback')?.textContent?.includes('preserved'),
        'Check error discarded history',
      );
    },
  );
  await check(
    'a portable transfer can be canceled by keyboard during install, and the same release can retry',
    async () => {
      await window.fixtureHarness.setUpdates(updates);
      button(text.updates).click();
      await tick();
      const hasCancel = () =>
        [...document.querySelectorAll('button')].some(
          (node) => node.textContent?.trim() === text.updateCancelDownload,
        );
      const hasStatus = (label: string) =>
        [...document.querySelectorAll('[role="status"]')].some(
          (node) => node.textContent?.trim() === label,
        );
      assert(!hasCancel(), 'Cancel was offered while idle');
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('installUpdate', { pending: true });
      outstanding.add('installUpdate');
      button(text.updateInstall).click();
      await untilCall('installUpdate');
      assert(!hasCancel(), 'Cancel was offered while consent was pending');
      const preparing: UpdateState = { ...updates, phase: 'downloading', progress: 50 };
      await window.fixtureHarness.setUpdates(preparing);
      await tick();
      assert(!hasCancel(), 'Cancel was offered without an active transfer');
      const downloadId = '42eca1f3-8051-4e89-9fd6-3f0ce4f88629';
      const transfer = { ...preparing, downloadId, progress: 65 };
      await window.fixtureHarness.setUpdates(transfer);
      await tick();
      const cancel = button(text.updateCancelDownload);
      assert(!cancel.disabled && button(text.updateInstall).disabled, 'Wrong transfer actions');
      assert(
        document.querySelector(`[role="progressbar"][aria-label="${text.updateDownloadProgress}"]`),
        'Download progress is unlabeled',
      );
      await window.fixtureHarness.setResponse('cancelUpdateDownload', { pending: true });
      outstanding.add('cancelUpdateDownload');
      cancel.focus();
      assert(document.activeElement === cancel, 'Cancel could not receive keyboard focus');
      let trustedActivation = false;
      cancel.addEventListener('click', (event) => (trustedActivation = event.isTrusted), {
        once: true,
      });
      await window.fixtureHarness.pressEnter();
      await untilCall('cancelUpdateDownload');
      assert(trustedActivation, 'Enter did not activate the native button');
      await tick();
      assert(cancel.disabled, 'Duplicate cancellation remained available');
      cancel.click();
      const calls = (await window.fixtureHarness.calls()).filter(
        (call) => call.method === 'cancelUpdateDownload',
      );
      assert(calls.length === 1 && calls[0]?.args[0] === downloadId, 'Wrong cancel request');
      await window.fixtureHarness.resolveResponse('cancelUpdateDownload', { value: 'accepted' });
      outstanding.delete('cancelUpdateDownload');
      await tick();
      assert(
        !hasStatus(text.updateDownloadCanceled),
        'Acknowledgment confirmed cancellation early',
      );
      await window.fixtureHarness.setUpdates({ ...preparing, downloadCancellation: 'canceling' });
      await tick();
      assert(!hasCancel() && hasStatus(text.updateCancelingDownload), 'Pending cleanup was hidden');
      assert(button(text.updateInstall).disabled, 'Retry was offered before cleanup');
      await window.fixtureHarness.setUpdates({ ...updates, downloadCancellation: 'canceled' });
      await window.fixtureHarness.resolveResponse('installUpdate', { value: transfer });
      outstanding.delete('installUpdate');
      await tick();
      assert(
        !hasCancel() &&
          hasStatus(text.updateDownloadCanceled) &&
          !button(text.updateInstall).disabled,
        'Stale install reply displaced confirmed cancellation or disabled retry',
      );
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('installUpdate', { value: updates });
      button(text.updateInstall).click();
      await untilCall('installUpdate');
      assert(
        (await window.fixtureHarness.calls()).find((call) => call.method === 'installUpdate')
          ?.args[0] === 'fixture-new-122',
        'Retry selected a different release',
      );
    },
  );
  await check(
    'rollback remains available with no backend and dispatches one opaque release ID',
    async () => {
      await window.fixtureHarness.setState({
        snapshot: null,
        connection: 'failed',
        error: 'Library failure fixture.',
        busy: false,
        authorityMode: null,
        administrationPending: false,
      });
      await window.fixtureHarness.setUpdates({
        ...updates,
        availableVersion: null,
        phase: 'error',
        error: 'No connection to GitHub.',
        releases: updates.releases.filter((release) => release.cached),
      });
      await tick();
      button(text.updates).click();
      await tick();
      const cachedCard = [...document.querySelectorAll<HTMLElement>('.update-release')].find(
        (card) => card.querySelector('h3')?.textContent === '1.2.0',
      );
      assert(cachedCard, 'Cached rollback version is missing');
      const rollback = button(text.updateRollback, cachedCard);
      assert(
        !rollback.disabled &&
          document.querySelector('.update-recovery-notice')?.textContent ===
            text.updateRecoveryHint,
        'Backend failure disabled recovery',
      );
      await window.fixtureHarness.clearCalls();
      await window.fixtureHarness.setResponse('installUpdate', { pending: true });
      outstanding.add('installUpdate');
      rollback.click();
      rollback.click();
      await untilCall('installUpdate');
      await tick();
      const calls = (await window.fixtureHarness.calls()).filter(
        (call) => call.method === 'installUpdate',
      );
      assert(calls.length === 1, 'Rollback repeated its request');
      assert(
        calls[0]!.args[0] === 'fixture-cached-120',
        'Rollback did not send the cached opaque release ID',
      );
      assert(rollback.disabled, 'Pending rollback action remains enabled');
      assert(
        document.querySelector('.updates-availability')?.textContent === text.updateLatestUnknown,
        'Failed check falsely reports the installed version as latest',
      );
      const checked = new Date(updates.lastCheckedAt!).toLocaleString('en-US', {
        dateStyle: 'short',
        timeStyle: 'short',
      });
      assert(
        document.querySelector('.updates-summary')?.textContent?.includes(checked),
        'Last successful check lost its date and time',
      );
      const guard = window.fixtureHarness.readGuard();
      assert(
        guard && !guard.dirtyDrafts && !guard.dialogOpen && !guard.pendingWork,
        'Install button pending state blocks its own host preflight',
      );
      await window.fixtureHarness.resolveResponse('installUpdate', { value: updates });
      outstanding.delete('installUpdate');
      await tick();
    },
  );
  await check(
    'dirty script drafts and active modal block update installation without losing edits',
    async () => {
      await window.fixtureHarness.setState(host);
      await window.fixtureHarness.setUpdates(updates);
      await tick();
      button(text.scripts).click();
      selectScript(host.snapshot!.scripts[0]!.id);
      await tick();
      const field = document.getElementById('draft-name') as HTMLInputElement;
      edit(field, 'Draft preserved before updating');
      await tick();
      button(text.updates).click();
      await tick();
      await window.fixtureHarness.clearCalls();
      assert(
        button(text.updateInstall).disabled &&
          document.querySelector('.update-recovery-notice')?.textContent === text.updateDirtyGuard,
        'Dirty metadata did not block update',
      );
      button(text.updateInstall).click();
      await tick();
      assert(
        !(await window.fixtureHarness.calls()).some((call) => call.method === 'installUpdate'),
        'Dirty draft dispatched installation',
      );
      button(text.scripts).click();
      await tick();
      assert(
        (document.getElementById('draft-name') as HTMLInputElement).value ===
          'Draft preserved before updating',
        'Update navigation discarded draft',
      );
      button(text.discard, document.querySelector('.inspector')!).click();
      await tick();
      button(text.updates).click();
      await tick();
      assert(!button(text.updateInstall).disabled, 'Discarded draft kept installation blocked');
      button(text.help).click();
      await tick();
      assert(button(text.updateInstall).disabled, 'Open modal did not block update action');
      button(text.close, document.querySelector('[role="dialog"]')!).click();
      await tick();
    },
  );
  await check('public updates expose no credential entry or token configuration', async () => {
    await window.fixtureHarness.setState(host);
    await window.fixtureHarness.setUpdates(updates);
    await tick();
    button(text.updates).click();
    await tick();
    assert(
      !document.querySelector('.update-access, #update-token'),
      'Credential controls remain visible',
    );
    assert(document.documentElement.lang === 'en-US', 'Application language is not English');
    assert(
      button('Check now') && document.querySelector('h1')?.textContent === 'Updates',
      'English update controls are missing',
    );
  });
  await window.fixtureHarness.setUpdates({ ...updates, availableVersion: null, releases: [] });
  await tick();
  return results;
}

export async function previewUpdates(
  recovery = false,
  testChannel = false,
): Promise<AssertionRecord[]> {
  const modal = document.querySelector('[role="dialog"]');
  if (modal) button(text.cancel, modal).click();
  await window.fixtureHarness.resetResponses();
  const updates = testChannel ? testUpdateFixture() : updateFixture();
  const cachedIndex = updates.releases.findIndex((release) => release.id === 'fixture-cached-120');
  updates.releases[cachedIndex] = {
    ...updates.releases[cachedIndex]!,
    notes: 'Stability and file recovery fixes. This version is preserved on this computer.',
  };
  if (recovery) {
    updates.availableVersion = null;
    updates.phase = 'error';
    updates.error = 'Online versions could not be checked. Local versions remain available.';
    updates.releases = [
      updates.releases[2]!,
      updates.releases[1]!,
      { ...updates.releases[0]!, canInstall: false },
    ];
  }
  await window.fixtureHarness.setUpdates(updates);
  await window.fixtureHarness.setState(
    recovery
      ? {
          snapshot: null,
          connection: 'failed',
          error: 'Recovery fixture.',
          busy: false,
          authorityMode: null,
          administrationPending: false,
        }
      : state(),
  );
  await tick();
  button(text.updates).click();
  await tick();
  const scroll = document.querySelector<HTMLElement>('.updates-page');
  if (scroll) scroll.scrollTop = 0;
  assert(
    scroll &&
      scroll.clientHeight > 0 &&
      scroll.scrollWidth <= scroll.clientWidth + 1 &&
      document.documentElement.scrollWidth <= innerWidth,
    'Update page overflows its viewport',
  );
  if (recovery && innerWidth < 700) {
    const history = document.querySelector<HTMLElement>('.update-history')!;
    scroll.scrollTop = history.getBoundingClientRect().top - scroll.getBoundingClientRect().top - 8;
    await tick();
  }
  return [
    {
      name: 'updates preview shows new version and cached rollback history',
      passed:
        !!document.querySelector('.update-release-list') &&
        (document.getElementById('update-channel') as HTMLSelectElement)?.value ===
          (testChannel ? 'test' : 'stable'),
    },
  ];
}

/** Deterministic visual states only: no update request or real transfer is dispatched. */
export async function previewUpdateDownload(canceling = false): Promise<AssertionRecord[]> {
  await previewUpdates();
  await window.fixtureHarness.setState({ ...state(), authorityMode: 'Administrator' });
  await window.fixtureHarness.clearCalls();
  await window.fixtureHarness.setUpdates({
    ...updateFixture(),
    phase: 'downloading',
    progress: 65,
    downloadId: canceling ? null : '42eca1f3-8051-4e89-9fd6-3f0ce4f88629',
    downloadCancellation: canceling ? 'canceling' : null,
  });
  await tick();
  const label = canceling ? text.updateCancelingDownload : text.updateDownloading;
  const status = [...document.querySelectorAll<HTMLElement>('[role="status"]')].find(
    (node) => node.textContent?.trim() === label,
  );
  assert(status, 'The transfer state has no visible status');
  const progress = document.querySelector<HTMLElement>(
    `[role="progressbar"][aria-label="${text.updateDownloadProgress}"]`,
  );
  assert(
    canceling ? !progress : !!progress,
    canceling ? 'Cleanup retained stale transfer progress' : 'The transfer progress is not labeled',
  );
  const cancels = [...document.querySelectorAll<HTMLButtonElement>('button')].filter(
    (node) => node.textContent?.trim() === text.updateCancelDownload,
  );
  if (canceling) assert(cancels.length === 0, 'Cleanup still offers another cancel action');
  else {
    assert(cancels.length === 1 && !cancels[0]!.disabled, 'An active transfer cannot be canceled');
    cancels[0]!.focus();
    assert(document.activeElement === cancels[0], 'Cancel cannot receive keyboard focus');
  }
  assert(
    button(text.updateInstall).disabled &&
      button(text.updateCheck).disabled &&
      (document.getElementById('update-channel') as HTMLSelectElement).disabled,
    'A transfer or its cleanup leaves competing update actions enabled',
  );
  const page = document.querySelector<HTMLElement>('.updates-page');
  assert(
    page &&
      page.scrollWidth <= page.clientWidth + 1 &&
      document.documentElement.scrollWidth <= innerWidth,
    'The transfer preview overflows its viewport',
  );
  for (const control of [status, ...(progress ? [progress] : []), ...cancels]) {
    const bounds = control.getBoundingClientRect();
    assert(
      bounds.width > 0 &&
        bounds.height > 0 &&
        bounds.left >= 0 &&
        bounds.right <= innerWidth &&
        bounds.top >= 0 &&
        bounds.bottom <= innerHeight,
      'A transfer status, progress or cancel control is outside the viewport',
    );
  }
  assert(
    !(await window.fixtureHarness.calls()).some((call) =>
      ['installUpdate', 'cancelUpdateDownload', 'checkUpdates', 'setUpdateChannel'].includes(
        call.method,
      ),
    ),
    'The transfer preview dispatched an update effect',
  );
  return [
    {
      name: canceling
        ? 'update cancellation cleanup is visible, bounded and does not offer another transfer action'
        : 'active update download shows labeled progress and a visible keyboard-focusable cancel action',
      passed: true,
    },
  ];
}
