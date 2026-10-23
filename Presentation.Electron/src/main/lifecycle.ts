/** Explicit activation remains intent throughout an asynchronous login decision. */
export class LifecycleIntent {
  private explicit:boolean;
  private login:boolean;
  private loginDispatched=false;
  private preferencesPending=false;
  constructor(initialLogin:boolean){this.login=initialLogin;this.explicit=!initialLogin;}
  requestExplicitActivation():void {this.explicit=true;}
  requestLogin():void {this.login=true;}
  requestPreferences():void {this.explicit=true;this.preferencesPending=true;}
  takePreferences(documentReady:boolean):boolean {if(!documentReady||!this.preferencesPending)return false;this.preferencesPending=false;return true;}
  get loginRequested():boolean {return this.login;}
  takeLogin():boolean {if(!this.login||this.loginDispatched)return false;this.loginDispatched=true;return true;}
  shouldShowAtStartup(showWindowAtLogin:boolean,trayRegistered:boolean):boolean {return this.explicit||!this.login||showWindowAtLogin||!trayRegistered;}
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

export const SHUTDOWN_DEADLINE_MS=15000;
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
