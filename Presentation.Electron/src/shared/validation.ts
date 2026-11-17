import type {
  ActivityQuery,
  ActivitySnapshot,
  CatalogResult,
  CollectionSnapshot,
  ElevationStatus,
  HotkeySnapshot,
  ImportPreview,
  Issue,
  ManagerSettings,
  ManagerSnapshot,
  OperationPlan,
  OperationResult,
  ProcessIdentity,
  ProcessSnapshot,
  RecycleAuthorization,
  RecyclePlan,
  ScriptFileIdentity,
  ScriptMetadata,
  ScriptOperation,
  ScriptQuery,
  ScriptSnapshot,
  SelectionContext,
  StartupContinuationOffer,
  WindowPlacement,
} from './contracts.js';
import { described, jsonSchema } from './json-schema.js';

/** Untyped checks remain useful for transport envelopes and custom predicates. */
export type Validator = (value: unknown) => boolean;
export type Schema<T> = (value: unknown) => value is T;
export type Validated<T> = T extends Schema<infer Value> ? Value : unknown;
type Rules = Record<string, Validator>;
type RequiredValue<T extends Rules> = { -readonly [Key in keyof T]: Validated<T[Key]> };
type OptionalValue<T extends Rules> = { -readonly [Key in keyof T]?: Validated<T[Key]> };
type RequiredKeys<T> = { [Key in keyof T]-?: {} extends Pick<T, Key> ? never : Key }[keyof T];
type OptionalKeys<T> = Exclude<keyof T, RequiredKeys<T>>;
type RequiredRules<T> = { [Key in RequiredKeys<T>]: Schema<T[Key]> };
type OptionalRules<T> = { [Key in OptionalKeys<T>]-?: Schema<Exclude<T[Key], undefined>> };

/** Pass-through boundaries keep the payload unknown until a later validator runs. */
export const unknownValue: Schema<unknown> = (_value): _value is unknown => true;
export const object = described(
  (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v),
  { type: 'object' },
);
export const text = (max = 32768): Schema<string> =>
  described(
    (v: unknown): v is string => typeof v === 'string' && v.length <= max && !v.includes('\0'),
    { type: 'string', maxLength: max, pattern: '^[^\\u0000]*$' },
  );
export const bool = described((v: unknown): v is boolean => typeof v === 'boolean', {
  type: 'boolean',
});
export const integer = described(
  (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v),
  { type: 'integer', minimum: Number.MIN_SAFE_INTEGER, maximum: Number.MAX_SAFE_INTEGER },
);
export const finite = described(
  (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v),
  { type: 'number' },
);
const revisionPattern = /^(0|[1-9][0-9]{0,18})$/;
const largestRevision = 9223372036854775807n;
export const decimal = described(
  (v: unknown): v is string =>
    typeof v === 'string' && revisionPattern.test(v) && BigInt(v) <= largestRevision,
  {
    type: 'string',
    pattern: revisionPattern.source,
    description:
      'Use the exact revision returned by Manager. The maximum is ' + largestRevision + '.',
  },
);
const guidPattern = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
export const guid = described(
  (v: unknown): v is string => typeof v === 'string' && guidPattern.test(v),
  { type: 'string', format: 'uuid', pattern: guidPattern.source },
);
export const oneOf = <const Values extends string[]>(...values: Values): Schema<Values[number]> =>
  described((v: unknown): v is Values[number] => typeof v === 'string' && values.includes(v), {
    type: 'string',
    enum: values,
  });
export const nullable = <Check extends Validator>(check: Check): Schema<Validated<Check> | null> =>
  described(
    (v: unknown): v is Validated<Check> | null => v === null || check(v),
    () => ({
      anyOf: [jsonSchema(check), { type: 'null' }],
    }),
  );
export const array = <Check extends Validator>(
  check: Check,
  max = 20000,
  min = 0,
): Schema<Validated<Check>[]> =>
  described(
    (v: unknown): v is Validated<Check>[] =>
      Array.isArray(v) && v.length >= min && v.length <= max && v.every(check),
    () => ({
      type: 'array',
      items: jsonSchema(check),
      maxItems: max,
      ...(min && { minItems: min }),
    }),
  );

export function shape<Required extends Rules, Optional extends Rules = {}>(
  required: Required,
  optional?: Optional,
): Schema<RequiredValue<Required> & OptionalValue<Optional>> {
  const optionalRules: Rules = optional ?? {};
  return described(
    (v: unknown): v is RequiredValue<Required> & OptionalValue<Optional> =>
      object(v) &&
      Object.keys(v).every((k) => Object.hasOwn(required, k) || Object.hasOwn(optionalRules, k)) &&
      Object.entries(required).every(([k, validate]) => Object.hasOwn(v, k) && validate(v[k])) &&
      Object.entries(optionalRules).every(
        ([k, validate]) => !Object.hasOwn(v, k) || validate(v[k]),
      ),
    () => ({
      type: 'object',
      properties: Object.fromEntries(
        Object.entries({ ...required, ...optionalRules }).map(([key, check]) => [
          key,
          jsonSchema(check),
        ]),
      ),
      required: Object.keys(required),
      additionalProperties: false,
    }),
  );
}

/** Every DTO field must have a compatible rule, including all optional fields. */
export function shapeFor<T extends object>() {
  return <Required extends RequiredRules<T>, Optional extends OptionalRules<T>>(
    required: Required & Record<Exclude<keyof Required, RequiredKeys<T>>, never>,
    ...optional: [OptionalKeys<T>] extends [never]
      ? []
      : [optional: Optional & Record<Exclude<keyof Optional, OptionalKeys<T>>, never>]
  ): Schema<T> => {
    const check = shape(required, optional[0]);
    return described(
      (value: unknown): value is T => check(value),
      () => jsonSchema(check),
    );
  };
}
export const operationNames = [
  'Start',
  'Stop',
  'Restart',
  'TogglePause',
  'ToggleSuspend',
  'Edit',
  'OpenFolder',
  'Recover',
  'RecycleFile',
] as const;
export const operation = oneOf(...operationNames);
export const outcome = oneOf(
  'Accepted',
  'Pending',
  'Confirmed',
  'Partial',
  'CancelledBeforeDispatch',
  'Failed',
  'Unknown',
);
export const reason = oneOf(
  'None',
  'InvalidPath',
  'MissingFile',
  'UnsupportedInterpreter',
  'AccessDenied',
  'ObservationIncomplete',
  'AlreadyRunning',
  'MultipleInstances',
  'IdentityChanged',
  'WindowUnverified',
  'CapabilityUnverified',
  'EffectOwnerUnavailable',
  'LegacyGuard',
  'PendingOperation',
  'StaleSelection',
  'ReadOnlySettings',
  'InvalidSettings',
  'UnsupportedSchema',
  'RecoveredBackup',
  'ConcurrentSettingsChange',
  'StorageFailure',
  'Cancelled',
  'DispatchFailed',
  'ExitNotObserved',
  'StateNotObservable',
  'RestartPartial',
  'LaunchUncertain',
  'StartupConsentRequired',
  'StartupEntryConflict',
  'StartupInactive',
  'StartupAlreadyProcessed',
  'ImportSourceChanged',
  'ImportInvalid',
  'ImportAvailable',
  'FolderLimitReached',
  'PartialAnalysis',
  'IncludeUnresolved',
  'IncludeLimitReached',
  'DynamicHotkeys',
  'PossibleConflict',
  'EditorUnavailable',
  'ActivityUnavailable',
  'InvalidMetadata',
  'ManualElevationRequired',
  'ExternalStartupConflict',
);
export const authorityMode = oneOf('Normal', 'Administrator');
export const elevationStatus = shapeFor<ElevationStatus>()({
  operationId: guid,
  revision: decimal,
  state: oneOf(
    'PendingApproval',
    'Ready',
    'CommitPending',
    'CommitAcked',
    'Cancelled',
    'Denied',
    'Failed',
    'Expired',
    'Completed',
    'Unknown',
  ),
  reason: nullable(reason),
  receiptId: nullable(guid),
  canResumeNormal: bool,
});
export const startupContinuationOffer = nullable(
  shapeFor<StartupContinuationOffer>()({
    operationId: guid,
    scriptNames: array(text(4096), 2000),
    canResume: bool,
    reason,
  }),
);
export const issue = shapeFor<Issue>()({
  code: reason,
  detail: nullable(text()),
  scriptId: nullable(guid),
});
export const identity = shapeFor<ProcessIdentity>()({
  pid: integer,
  creationFileTime: decimal,
  executablePath: text(),
  scriptPath: text(),
  userSid: text(256),
  sessionId: integer,
  scriptHash: text(128),
  interpreterHash: text(128),
});
export const processSnapshot = shapeFor<ProcessSnapshot>()(
  {
    identity,
    origin: oneOf('None', 'Manager', 'External', 'Recovered'),
    interpreterVersion: text(256),
    startedAt: text(128),
    paused: nullable(bool),
    suspended: nullable(bool),
    windowVerified: bool,
  },
  { controlUnavailableReason: nullable(oneOf('AccessDenied', 'ManualElevationRequired')) },
);
export const hotkey = shapeFor<HotkeySnapshot>()(
  {
    chord: text(2048),
    normalizedChord: text(2048),
    origin: oneOf('Parsed', 'Manual', 'Unanalysed'),
    context: nullable(text()),
    sourcePath: nullable(text()),
    line: nullable(integer),
    isRemap: bool,
    possibleConflict: bool,
  },
  { declaredGesture: nullable(oneOf('DoubleClick')) },
);
export const script = shapeFor<ScriptSnapshot>()(
  {
    id: guid,
    path: text(),
    friendlyName: text(4096),
    description: text(),
    collectionId: nullable(guid),
    runAtLogin: nullable(bool),
    runAsAdministrator: bool,
    launchBlocked: bool,
    status: oneOf('Stopped', 'Running', 'Unknown', 'Multiple', 'Missing', 'Blocked'),
    process: nullable(processSnapshot),
    hotkeys: array(hotkey),
    coverage: oneOf('Partial', 'Unanalysable'),
    issues: array(issue),
  },
  { headerDescription: nullable(text(4000)) },
);
export const placement = shapeFor<WindowPlacement>()({
  x: integer,
  y: integer,
  width: integer,
  height: integer,
  maximized: bool,
  dpi: finite,
});
export const settings = shapeFor<ManagerSettings>()({
  interpreterPath: nullable(text()),
  editorPath: nullable(text()),
  editorArguments: array(text(4096), 128),
  launchManagerAtLogin: bool,
  launchManagerAsAdministrator: bool,
  showWindowAtLogin: bool,
  startInTray: bool,
  theme: oneOf('System', 'Light', 'Dark'),
  closeBehavior: oneOf('HideToTray', 'Exit'),
  window: placement,
});
export const activity = shapeFor<ActivitySnapshot>()({
  id: guid,
  timestamp: text(128),
  scriptId: nullable(guid),
  operation: nullable(operation),
  outcome,
  code: reason,
  detail: nullable(text()),
});
export const snapshot = shapeFor<ManagerSnapshot>()(
  {
    revision: decimal,
    scripts: array(script),
    collections: array(shapeFor<CollectionSnapshot>()({ id: guid, name: text(4096) })),
    settings,
    activity: array(activity),
    issues: array(issue),
    readOnly: bool,
    effectsAvailable: bool,
    observedAt: nullable(text(128)),
  },
  {
    runningCount: integer,
    problemCount: integer,
    normalLaunchUnavailableReason: nullable(oneOf('AccessDenied', 'CapabilityUnverified')),
  },
);
export const query = shapeFor<ScriptQuery>()({
  search: text(4096),
  status: oneOf('All', 'Running', 'Stopped', 'Problems'),
  sort: oneOf('Name', 'Status', 'Collection'),
  collectionId: nullable(guid),
  version: decimal,
});
export const selection = shapeFor<SelectionContext>()({
  scriptId: guid,
  query,
  stateRevision: decimal,
});
export const operationPlan = shapeFor<OperationPlan>()({
  correlationId: guid,
  scriptId: guid,
  operation,
  stateRevision: decimal,
  queryVersion: decimal,
  expectedIdentity: nullable(identity),
  allowed: bool,
  requiresConfirmation: bool,
  reason,
  selection,
});
export const operationResult = shapeFor<OperationResult>()(
  { outcome, code: reason, detail: nullable(text()), correlationId: nullable(guid) },
  { succeeded: bool },
);
export const scriptFileIdentity = shapeFor<ScriptFileIdentity>()({
  path: text(),
  fileId: text(512),
  hash: text(128),
});
export const recyclePlan = shapeFor<RecyclePlan>()({
  operationId: guid,
  scriptId: guid,
  path: text(),
  file: nullable(scriptFileIdentity),
  expectedIdentity: nullable(identity),
  selection,
  allowed: bool,
  reason,
});
export const recycleAuthorization = shapeFor<RecycleAuthorization>()({
  result: operationResult,
  file: nullable(scriptFileIdentity),
  stopped: bool,
});
export const catalogResult = shapeFor<CatalogResult>()({
  result: operationResult,
  added: array(guid),
  duplicateCount: integer,
  issues: array(issue),
});
export const metadata = shapeFor<ScriptMetadata>()({
  friendlyName: text(4096),
  description: text(),
  collectionId: nullable(guid),
  runAtLogin: nullable(bool),
  runAsAdministrator: bool,
  launchBlocked: bool,
  manualHotkeys: array(text(2048), 512),
});
export const activityQuery = shapeFor<ActivityQuery>()(
  {},
  { scriptId: nullable(guid), outcome: nullable(outcome), search: text(4096) },
);
export const importPreview = shapeFor<ImportPreview>()({
  sourcePath: text(),
  sourceHash: text(128),
  scriptCount: integer,
  issues: array(issue),
  canImport: bool,
});
export function requireValid<T>(value: unknown, validator: Schema<T>, label: string): T {
  if (!validator(value)) throw new Error(`Invalid ${label}`);
  return value;
}
export const assertSettings = (v: unknown): ManagerSettings =>
  requireValid(v, settings, 'settings');
export const assertMetadata = (v: unknown): ScriptMetadata => requireValid(v, metadata, 'metadata');
export const assertSelection = (v: unknown): SelectionContext =>
  requireValid(v, selection, 'selection');
export const assertOperation = (v: unknown): ScriptOperation =>
  requireValid(v, operation, 'operation');
export const assertActivityQuery = (v: unknown): ActivityQuery =>
  requireValid(v, activityQuery, 'activity query');

/** JSON.parse alone silently accepts duplicate properties, including escaped aliases. */
export function parseStrictJson(source: string): unknown {
  const parsed: unknown = JSON.parse(source);
  let i = 0;
  const ws = () => {
    while (/\s/.test(source[i] ?? '') && i < source.length) i++;
  };
  const string = (): string => {
    const start = i++;
    while (i < source.length) {
      const c = source[i++];
      if (c === '\\') i++;
      else if (c === '"') return JSON.parse(source.slice(start, i)) as string;
    }
    throw new Error('Unterminated JSON string');
  };
  const value = (): void => {
    ws();
    if (source[i] === '{') {
      i++;
      ws();
      const keys = new Set<string>();
      while (source[i] !== '}') {
        const key = string();
        if (keys.has(key)) throw new Error('Duplicate JSON property');
        keys.add(key);
        ws();
        i++;
        value();
        ws();
        if (source[i] !== ',') break;
        i++;
        ws();
      }
      i++;
    } else if (source[i] === '[') {
      i++;
      ws();
      while (source[i] !== ']') {
        value();
        ws();
        if (source[i] !== ',') break;
        i++;
      }
      i++;
    } else if (source[i] === '"') string();
    else {
      while (i < source.length && !/[\s,\]}]/.test(source[i]!)) i++;
    }
  };
  value();
  return parsed;
}
