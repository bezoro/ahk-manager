import { randomBytes } from 'node:crypto';
import path from 'node:path';
import type { IpcMainEvent, IpcMainInvokeEvent, WebContents, WebFrameMain } from 'electron';
import { object, shape, text } from '../shared/validation.js';

export const APP_ORIGIN = 'ahk-manager://app';
export const APP_URL = `${APP_ORIGIN}/index.html`;
export const CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; frame-ancestors 'none'; worker-src 'none'; media-src 'none'";
// Fluent's renderer creates styles dynamically. Script remains self-only without eval or inline code.
export function assetFromUrl(raw:string,allowlist:ReadonlySet<string>):string|null {
  try{
    const url=new URL(raw);
    if(url.protocol!=='ahk-manager:'||url.host!=='app'||url.username||url.password||url.search||url.hash)return null;
    const name=decodeURIComponent(url.pathname).slice(1);
    if(!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(name)||name.split('/').some(x=>x==='.'||x==='..'||!x)||name.includes('\\'))return null;
    return allowlist.has(name)?name:null;
  }catch{return null;}
}
export interface DocumentLease { token:string; generation:number; processId:number; routingId:number }
export class DocumentGuard {
  private generation=0;
  private lease:DocumentLease|null=null;
  constructor(private readonly contents:WebContents){}
  get connected():boolean {return this.lease!==null&&this.current(this.lease);}
  invalidate():void { this.generation++;this.lease=null; }
  private validFrame(event:IpcMainEvent|IpcMainInvokeEvent):WebFrameMain {
    const frame=event.senderFrame;
    if(this.contents.isDestroyed()||event.sender!==this.contents||!frame||frame!==this.contents.mainFrame||frame.url!==APP_URL||frame.isDestroyed())throw new Error('Untrusted IPC sender');
    return frame;
  }
  connect(event:IpcMainInvokeEvent):DocumentLease {
    const frame=this.validFrame(event);
    this.lease={token:randomBytes(32).toString('hex'),generation:this.generation,processId:frame.processId,routingId:frame.routingId};
    return {...this.lease};
  }
  validate(event:IpcMainEvent|IpcMainInvokeEvent,envelope:unknown):{lease:DocumentLease;body:unknown} {
    const frame=this.validFrame(event);
    if(!shape({token:text(64),body:()=>true})(envelope)||!object(envelope)||!this.lease||envelope.token!==this.lease.token||frame.processId!==this.lease.processId||frame.routingId!==this.lease.routingId)throw new Error('Stale IPC document');
    return {lease:{...this.lease},body:envelope.body};
  }
  current(lease:DocumentLease):boolean {
    if(!this.lease||this.contents.isDestroyed()||this.contents.mainFrame.isDestroyed())return false;
    const frame=this.contents.mainFrame;
    return lease.token===this.lease.token&&lease.generation===this.generation&&frame.url===APP_URL&&frame.processId===lease.processId&&frame.routingId===lease.routingId;
  }
  send(channel:string,body:unknown):void {
    const lease=this.lease;
    if(lease&&this.current(lease))this.contents.mainFrame.send(channel,{token:lease.token,body});
  }
}
export function packagedBackend(appDirectory:string):string {
  if(!path.isAbsolute(appDirectory))throw new Error('Application path must be absolute');
  return path.join(appDirectory,'backend','AhkManager.Backend.exe');
}
