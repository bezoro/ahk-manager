import type { Issue, ManagerSettings, OperationResult } from '../shared/contracts.js';

interface WindowActivationBoundary {
  isMinimized():boolean;
  restore():void;
  show():void;
  maximize():void;
  focus():void;
}

/** Explicit activation remains intent throughout an asynchronous startup decision. */
export class LifecycleIntent {
  private explicit=false;
  private login:boolean;
  private loginDispatched=false;
  private preferencesPending=false;
  private restoreMaximized=false;
  constructor(private readonly initialLogin:boolean,initialExplicit=false){this.login=initialLogin;this.explicit=initialExplicit;}
  requestExplicitActivation():void {this.explicit=true;}
  requestLogin():void {this.login=true;}
  requestPreferences():void {this.explicit=true;this.preferencesPending=true;}
  takePreferences(documentReady:boolean):boolean {if(!documentReady||!this.preferencesPending)return false;this.preferencesPending=false;return true;}
  get loginRequested():boolean {return this.login;}
  takeLogin():boolean {if(!this.login||this.loginDispatched)return false;this.loginDispatched=true;return true;}
  deferMaximizedRestore(maximized:boolean):void {this.restoreMaximized=maximized;}
  get pendingMaximizedRestore():boolean {return this.restoreMaximized;}
  showWindow(window:WindowActivationBoundary):void {
    if(window.isMinimized())window.restore();
    window.show();
    if(this.restoreMaximized){window.maximize();this.restoreMaximized=false;}
    window.focus();
  }
  shouldShowAtStartup(settings:ManagerSettings|null|undefined,trayRegistered:boolean,recoveryRequired=false,issues:readonly Issue[]=[]):boolean {
    return this.explicit||!trayRegistered||!settings||recoveryRequired||issues.some(issue=>issue.code==='PendingOperation'&&issue.scriptId===null)||(!settings.startInTray&&(!this.initialLogin||settings.showWindowAtLogin));
  }
  closeAction(preference:'HideToTray'|'Exit',trayRegistered:boolean):'hide'|'exit' {return preference==='HideToTray'&&trayRegistered?'hide':'exit';}
}

/** All competing exit paths cancel stopping; none silently changes its meaning. */
export class StopExitIntent {
  active=false;
  cancelled=false;
  begin():boolean {if(this.active)return false;this.active=true;this.cancelled=false;return true;}
  cancel():void {if(this.active)this.cancelled=true;}
  complete(canExit:boolean):boolean {const exit=this.active&&canExit&&!this.cancelled;this.active=false;return exit;}
}

/** A partial login must stay visible even when startup preferences hide the window. */
export async function reportStartupResult(result:OperationResult,boundary:{show():void;warn(result:OperationResult):Promise<void>}):Promise<void> {
  if(!['Partial','Failed','Unknown'].includes(result.outcome))return;
  boundary.show();await boundary.warn(result);
}

export const SHUTDOWN_DEADLINE_MS=15000;
/** The administrative route is a foreground intent and can never become a login request. */
export function administrationCli(argv:readonly string[]):'none'|'request'|'invalid' {
  const count=argv.filter(value=>value==='--request-administrator').length;
  if(!count)return 'none';
  return count===1&&!argv.some(value=>value==='--login'||value==='--background')?'request':'invalid';
}
/** A descriptor selects the blocked adoption state; it never grants authority. */
export function startupAdoptionCli(argv:readonly string[]):{descriptor:string|null;invalid:boolean} {
  const values=argv.filter(value=>value==='--startup-adoption'||value.startsWith('--startup-adoption='));
  if(!values.length)return {descriptor:null,invalid:false};
  const descriptor=values[0]?.slice('--startup-adoption='.length)??'';
  const valid=values.length===1&&values[0]!.startsWith('--startup-adoption=')&&/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\|[0-9a-f]{64}\|[1-9][0-9]{0,9}\|[1-9][0-9]{0,18}\|[0-9a-f]{64}$/.test(descriptor)&&!argv.some(value=>['--login','--background','--request-administrator'].includes(value));
  return {descriptor:valid?descriptor:null,invalid:!valid};
}
/** A completed unknown receipt opens recovery; domain evidence still blocks script effects. */
export function startupAdoptionBlocksInteraction(result:OperationResult):boolean {return result.outcome==='Pending'||result.outcome==='Accepted';}
export async function withinDeadline<T>(request:()=>Promise<T>,milliseconds:number,schedule:(fn:()=>void,ms:number)=>()=>void=(fn,ms)=>{const timer=setTimeout(fn,ms);return ()=>clearTimeout(timer);}):Promise<T> {
  let cancel=()=>{};
  try{return await Promise.race([request(),new Promise<never>((_resolve,reject)=>{cancel=schedule(()=>reject(new Error('DeadlineExceeded')),milliseconds);})]);}
  finally{cancel();}
}

export interface ShutdownBoundary {
  drain():Promise<void>;
  backendPresent():boolean;
  backendExited():boolean;
  persistPlacement():Promise<void>;
  shutdownBackend():Promise<void>;
  reportPlacementFailure(error:unknown):void;
}
/** Placement is optional persistence; observed child exit is the shutdown authority. */
export async function orderlyShutdown(boundary:ShutdownBoundary):Promise<void> {
  await boundary.drain();
  if(!boundary.backendPresent()||boundary.backendExited())return;
  try{await boundary.persistPlacement();}catch(error){boundary.reportPlacementFailure(error);}
  if(boundary.backendExited())return;
  await boundary.shutdownBackend();
}
