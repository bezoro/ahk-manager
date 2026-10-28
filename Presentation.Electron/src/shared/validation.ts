import type { ActivityQuery, ManagerSettings, ScriptMetadata, ScriptOperation, SelectionContext } from './contracts.js';

export type Validator = (value: unknown) => boolean;
export const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
export const text = (max = 32768): Validator => v => typeof v === 'string' && v.length <= max && !v.includes('\0');
export const bool: Validator = v => typeof v === 'boolean';
export const integer: Validator = v => typeof v === 'number' && Number.isSafeInteger(v);
export const finite: Validator = v => typeof v === 'number' && Number.isFinite(v);
export const decimal: Validator = v => typeof v === 'string' && /^(0|[1-9][0-9]{0,18})$/.test(v) && BigInt(v) <= 9223372036854775807n;
export const guid: Validator = v => typeof v === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
export const oneOf = (...values: string[]): Validator => v => typeof v === 'string' && values.includes(v);
export const nullable = (check: Validator): Validator => v => v === null || check(v);
export const array = (check: Validator, max = 20000): Validator => v => Array.isArray(v) && v.length <= max && v.every(check);
export function shape(required: Record<string, Validator>, optional: Record<string, Validator> = {}): Validator {
  return v => object(v) && Object.keys(v).every(k => Object.hasOwn(required, k) || Object.hasOwn(optional, k)) &&
    Object.entries(required).every(([k, validate]) => Object.hasOwn(v, k) && validate(v[k])) &&
    Object.entries(optional).every(([k, validate]) => !Object.hasOwn(v, k) || validate(v[k]));
}
export const operation = oneOf('Start', 'Stop', 'Restart', 'TogglePause', 'ToggleSuspend', 'Edit', 'OpenFolder', 'Recover', 'RecycleFile');
export const outcome = oneOf('Accepted', 'Pending', 'Confirmed', 'Partial', 'CancelledBeforeDispatch', 'Failed', 'Unknown');
export const reason = oneOf('None','InvalidPath','MissingFile','UnsupportedInterpreter','AccessDenied','ObservationIncomplete','AlreadyRunning','MultipleInstances','IdentityChanged','WindowUnverified','CapabilityUnverified','EffectOwnerUnavailable','LegacyGuard','PendingOperation','StaleSelection','ReadOnlySettings','InvalidSettings','UnsupportedSchema','RecoveredBackup','ConcurrentSettingsChange','StorageFailure','Cancelled','DispatchFailed','ExitNotObserved','StateNotObservable','RestartPartial','LaunchUncertain','StartupConsentRequired','StartupEntryConflict','StartupInactive','StartupAlreadyProcessed','ImportSourceChanged','ImportInvalid','ImportAvailable','FolderLimitReached','PartialAnalysis','IncludeUnresolved','IncludeLimitReached','DynamicHotkeys','PossibleConflict','EditorUnavailable','ActivityUnavailable','InvalidMetadata','ManualElevationRequired','ExternalStartupConflict');
export const authorityMode = oneOf('Normal','Administrator');
export const elevationStatus = shape({operationId:guid,revision:decimal,state:oneOf('PendingApproval','Ready','CommitPending','CommitAcked','Cancelled','Denied','Failed','Expired','Completed','Unknown'),reason:nullable(reason),receiptId:nullable(guid),canResumeNormal:bool});
export const startupContinuationOffer = nullable(shape({operationId:guid,scriptNames:array(text(4096),2000),canResume:bool,reason}));
export const issue = shape({code: reason, detail: nullable(text()), scriptId: nullable(guid)});
export const identity = shape({pid: integer, creationFileTime: decimal, executablePath: text(), scriptPath: text(), userSid: text(256), sessionId: integer, scriptHash: text(128), interpreterHash: text(128)});
export const processSnapshot = shape({identity, origin: oneOf('None','Manager','External','Recovered'), interpreterVersion: text(256), startedAt: text(128), paused: nullable(bool), suspended: nullable(bool), windowVerified: bool},{controlUnavailableReason:nullable(oneOf('AccessDenied','ManualElevationRequired'))});
export const hotkey = shape({chord:text(2048),normalizedChord:text(2048),origin:oneOf('Parsed','Manual','Unanalysed'),context:nullable(text()),sourcePath:nullable(text()),line:nullable(integer),isRemap:bool,possibleConflict:bool},{declaredGesture:nullable(oneOf('DoubleClick'))});
export const script = shape({id:guid,path:text(),friendlyName:text(4096),description:text(),collectionId:nullable(guid),runAtLogin:nullable(bool),runAsAdministrator:bool,launchBlocked:bool,status:oneOf('Stopped','Running','Unknown','Multiple','Missing','Blocked'),process:nullable(processSnapshot),hotkeys:array(hotkey),coverage:oneOf('Partial','Unanalysable'),issues:array(issue)},{headerDescription:nullable(text(4000))});
export const placement = shape({x:integer,y:integer,width:integer,height:integer,maximized:bool,dpi:finite});
export const settings = shape({interpreterPath:nullable(text()),editorPath:nullable(text()),editorArguments:array(text(4096),128),launchManagerAtLogin:bool,launchManagerAsAdministrator:bool,showWindowAtLogin:bool,startInTray:bool,theme:oneOf('System','Light','Dark'),closeBehavior:oneOf('HideToTray','Exit'),window:placement});
export const activity = shape({id:guid,timestamp:text(128),scriptId:nullable(guid),operation:nullable(operation),outcome,code:reason,detail:nullable(text())});
export const snapshot = shape({revision:decimal,scripts:array(script),collections:array(shape({id:guid,name:text(4096)})),settings,activity:array(activity),issues:array(issue),readOnly:bool,effectsAvailable:bool,observedAt:nullable(text(128))},{runningCount:integer,problemCount:integer,normalLaunchUnavailableReason:nullable(oneOf('AccessDenied','CapabilityUnverified'))});
export const query = shape({search:text(4096),status:oneOf('All','Running','Stopped','Problems'),sort:oneOf('Name','Status','Collection'),collectionId:nullable(guid),version:decimal});
export const selection = shape({scriptId:guid,query,stateRevision:decimal});
export const operationPlan = shape({correlationId:guid,scriptId:guid,operation,stateRevision:decimal,queryVersion:decimal,expectedIdentity:nullable(identity),allowed:bool,requiresConfirmation:bool,reason,selection});
export const operationResult = shape({outcome,code:reason,detail:nullable(text()),correlationId:nullable(guid)},{succeeded:bool});
export const scriptFileIdentity = shape({path:text(),fileId:text(512),hash:text(128)});
export const recyclePlan = shape({operationId:guid,scriptId:guid,path:text(),file:nullable(scriptFileIdentity),expectedIdentity:nullable(identity),selection,allowed:bool,reason});
export const recycleAuthorization = shape({result:operationResult,file:nullable(scriptFileIdentity),stopped:bool});
export const catalogResult = shape({result:operationResult,added:array(guid),duplicateCount:integer,issues:array(issue)});
export const metadata = shape({friendlyName:text(4096),description:text(),collectionId:nullable(guid),runAtLogin:nullable(bool),runAsAdministrator:bool,launchBlocked:bool,manualHotkeys:array(text(2048),512)});
export const activityQuery = shape({},{scriptId:nullable(guid),outcome:nullable(outcome),search:text(4096)});
export const importPreview = shape({sourcePath:text(),sourceHash:text(128),scriptCount:integer,issues:array(issue),canImport:bool});
export function requireValid<T>(value: unknown, validator: Validator, label: string): T {
  if (!validator(value)) throw new Error(`Invalid ${label}`);
  return value as T;
}
export const assertSettings = (v: unknown): ManagerSettings => requireValid(v, settings, 'settings');
export const assertMetadata = (v: unknown): ScriptMetadata => requireValid(v, metadata, 'metadata');
export const assertSelection = (v: unknown): SelectionContext => requireValid(v, selection, 'selection');
export const assertOperation = (v: unknown): ScriptOperation => requireValid(v, operation, 'operation');
export const assertActivityQuery = (v: unknown): ActivityQuery => requireValid(v, activityQuery, 'activity query');

/** JSON.parse alone silently accepts duplicate properties, including escaped aliases. */
export function parseStrictJson(source: string): unknown {
  const parsed: unknown = JSON.parse(source);
  let i = 0;
  const ws = () => { while (/\s/.test(source[i] ?? '') && i < source.length) i++; };
  const string = (): string => {
    const start = i++;
    while (i < source.length) { const c = source[i++]; if (c === '\\') i++; else if (c === '"') return JSON.parse(source.slice(start, i)) as string; }
    throw new Error('Unterminated JSON string');
  };
  const value = (): void => {
    ws();
    if (source[i] === '{') {
      i++; ws(); const keys = new Set<string>();
      while (source[i] !== '}') {
        const key = string(); if (keys.has(key)) throw new Error('Duplicate JSON property'); keys.add(key);
        ws(); i++; value(); ws(); if (source[i] !== ',') break; i++; ws();
      }
      i++;
    } else if (source[i] === '[') {
      i++; ws(); while (source[i] !== ']') { value(); ws(); if (source[i] !== ',') break; i++; }
      i++;
    } else if (source[i] === '"') string();
    else { while (i < source.length && !/[\s,\]}]/.test(source[i]!)) i++; }
  };
  value();
  return parsed;
}
