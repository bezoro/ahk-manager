import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { randomBytes } from 'node:crypto';
import path from 'node:path';
import {hostText as t} from '../shared/host-pt-BR.js';
import { TextDecoder } from 'node:util';
import type { AuthorityMode, ManagerSnapshot, OperationPlan, OperationResult } from '../shared/contracts.js';
import { array, activity, authorityMode, elevationStatus, bool, catalogResult, guid, importPreview, integer, issue, nullable, object, oneOf, operationPlan, operationResult, parseStrictJson, reason, requireValid, shape, snapshot, text, type Validator } from '../shared/validation.js';

export const MAX_FRAME = 8 * 1024 * 1024;
export const MAX_PENDING = 32;
const protocolCode = oneOf('Ok','InvalidFrame','InvalidRequest','NotAuthenticated','ParentMismatch','UnknownAction','QueueFull','IdConflict','RequestExpired','FrameTooLarge','ShuttingDown','InternalFailure');
const envelope = shape({version:v=>v===1,kind:oneOf('response','snapshot','parentProof'),id:nullable(text(20)),session:text(64),action:text(64),code:protocolCode,payload:()=>true,error:nullable(text(4096))});
const migration = shape({previewId:guid,sourceRoot:text(),sourceFingerprint:text(128),fileCount:integer,pendingOperations:integer,canMigrate:bool,issues:array(issue)});
const results: Record<string, Validator> = {
  beginAdministration:elevationStatus,administrationStatus:elevationStatus,commitAdministration:elevationStatus,cancelAdministration:elevationStatus,
  hello:shape({protocolVersion:v=>v===1,profileRoot:text(),hostExecutable:text()}),
  load:snapshot,snapshot,refresh:snapshot,addFiles:catalogResult,addFolder:catalogResult,
  remove:operationResult,saveMetadata:operationResult,saveCollection:operationResult,removeCollection:operationResult,
  prepare:shape({planId:guid,plan:operationPlan}),perform:operationResult,saveSettings:operationResult,runLogin:operationResult,
  activity:array(activity),previewImport:shape({previewId:guid,preview:importPreview}),import:operationResult,
  restoreBackup:operationResult,recoverPending:operationResult,previewMigration:migration,migrate:operationResult,shutdown:operationResult,
  previewStartupTransfer:shape({previewId:guid,oldExecutable:nullable(text()),newExecutable:text(),oldCommand:nullable(text()),newCommand:text(),canTransfer:bool,reason}),transferStartup:operationResult,
};
export interface PreparedOperation { planId: string; plan: OperationPlan }
export interface ProfileMigrationPreview { previewId:string; sourceRoot:string; sourceFingerprint:string; fileCount:number; pendingOperations:number; canMigrate:boolean; issues:import('../shared/contracts.js').Issue[] }
export class UnknownAfterDispatchError extends Error {
  constructor(readonly action:string,readonly requestId:string) { super(t.dispatchedUnknown(action,requestId)); }
}
interface Pending { action:string; resolve:(v:unknown)=>void; reject:(e:Error)=>void; timer:ReturnType<typeof setTimeout> }
export interface BackendOptions { executable:string; hostExecutable:string; createChild?:typeof spawn; timeoutMs?:number; session?:string }
export class BackendClient extends EventEmitter {
  private child: ChildProcessWithoutNullStreams | null = null;
  private pending = new Map<string,Pending>();
  private expired = new Map<string,string>();
  private buffer = Buffer.alloc(0);
  private sequence = 0n;
  private failure: Error | null = null;
  private exitPromise:Promise<{code:number|null;signal:NodeJS.Signals|null}> | null = null;
  private proofResolve:((value:Record<string,unknown>)=>void)|null = null;
  private proofReject:((error:Error)=>void)|null = null;
  private spawnConfirmed=false;
  readonly session: string;
  exited = false;
  ready = false;
  profileRoot:string|null = null;
  authorityMode:AuthorityMode|null=null;
  get pid():number|null { return this.child?.pid ?? null; }
  constructor(private readonly options:BackendOptions) { super();this.session=options.session??randomBytes(32).toString('hex'); }
  async start():Promise<void> {
    if(this.child)throw new Error('Backend session already started');
    const spawnChild = this.options.createChild ?? spawn;
    try{this.child = spawnChild(this.options.executable,['--parent-pid',String(process.pid),'--session',this.session],{shell:false,windowsHide:true,detached:false,stdio:['pipe','pipe','pipe'],cwd:path.dirname(this.options.executable)}) as ChildProcessWithoutNullStreams;}
    catch(error){this.exited=true;this.fail(error instanceof Error?error:new Error('Backend spawn failed'));throw error;}
    this.child.once('spawn',()=>{this.spawnConfirmed=true;});
    this.exitPromise = new Promise(resolve=>{
      this.child!.once('exit',(code,signal)=>{this.exited=true;this.ready=false;resolve({code,signal});this.fail(new Error(t.backendClosed(code??signal)));this.emit('exited',{code,signal});});
      this.child!.once('error',error=>{
        // Node emits error without exit for a failed OS spawn. A PID or a spawn event
        // is positive evidence of a child and must keep shutdown/retry guarded.
        if(!this.spawnConfirmed&&this.child?.pid===undefined){this.exited=true;this.ready=false;resolve({code:null,signal:null});this.emit('exited',{code:null,signal:null,spawnFailed:true});}
        this.fail(error);
      });
    });
    this.child.stdin.on('error',error=>this.fail(error));
    this.child.stdout.on('data',(chunk:Buffer)=>this.receive(chunk));
    this.child.stdout.once('end',()=>{if(this.buffer.length)this.fail(new Error('Incomplete backend frame'));else if(!this.exited)this.fail(new Error(t.backendChannelClosed));});
    // Diagnostic bytes are deliberately not exposed to the page or persisted with profile data.
    let diagnosticBytes=0;
    this.child.stderr.on('data',(chunk:Buffer)=>{diagnosticBytes+=chunk.length;if(diagnosticBytes>65536)this.fail(new Error('Backend diagnostic limit exceeded'));});
    const proof = await new Promise<Record<string,unknown>>((resolve,reject)=>{
      this.proofResolve=resolve;this.proofReject=reject;
      const timer=setTimeout(()=>reject(new Error('Backend authentication deadline exceeded')),this.options.timeoutMs??15000);
      const finish=resolve;this.proofResolve=value=>{clearTimeout(timer);finish(value);};
    }).catch(error=>{this.fail(error instanceof Error?error:new Error('Authentication failed'));throw error;});
    if(proof.parentPid!==process.pid||proof.childPid!==this.child.pid||typeof proof.parentCreated!=='string'||!/^\d{1,19}$/.test(proof.parentCreated)||typeof proof.parentExecutable!=='string'||path.resolve(proof.parentExecutable).toLowerCase()!==path.resolve(this.options.hostExecutable).toLowerCase()){const error=new Error('Backend parent proof mismatch');this.fail(error);throw error;}
    const hello = await this.request<{protocolVersion:number;profileRoot:string;hostExecutable:string}>('hello',{parentPid:process.pid,parentCreated:proof.parentCreated});
    if(path.resolve(hello.hostExecutable).toLowerCase()!==path.resolve(this.options.hostExecutable).toLowerCase()){const error=new Error('Authenticated host mismatch');this.fail(error);throw error;}
    this.authorityMode=proof.authorityMode as AuthorityMode;
    this.profileRoot=hello.profileRoot;
    this.ready=true;
  }
  request<T>(action:string,payload:unknown,timeoutMs=this.options.timeoutMs??30000):Promise<T> {
    if(this.failure||!this.child||this.exited)return Promise.reject(this.failure??new Error('Backend unavailable'));
    if(!Object.hasOwn(results,action))return Promise.reject(new Error('Unknown backend action'));
    if(this.pending.size>=MAX_PENDING)return Promise.reject(new Error('Backend queue full'));
    const id=(++this.sequence).toString();
    if(this.sequence>18446744073709551615n)return Promise.reject(new Error('Session request ID exhausted'));
    const frame=Buffer.from(JSON.stringify({version:1,id,session:this.session,action,payload})+'\n','utf8');
    if(frame.length-1>MAX_FRAME)return Promise.reject(new Error('Backend request frame too large'));
    return new Promise<T>((resolve,reject)=>{
      const timer=setTimeout(()=>{
        this.pending.delete(id);this.expired.set(id,action);
        const error=new UnknownAfterDispatchError(action,id);reject(error);
        if(!['beginAdministration','administrationStatus','commitAdministration','cancelAdministration'].includes(action))this.fail(error);
        else if(this.expired.size>256)this.fail(new Error('Expired response limit exceeded'));
      },timeoutMs);
      this.pending.set(id,{action,resolve:resolve as (v:unknown)=>void,reject,timer});
      // Node streams retain ordering and honor bounded pending intake; effects are never retried.
      this.child!.stdin.write(frame,error=>{if(error)this.fail(error);});
    });
  }
  private receive(chunk:Buffer):void {
    if(this.failure)return;
    let offset=0;
    while(offset<chunk.length){
      const newline=chunk.indexOf(10,offset);
      const end=newline<0?chunk.length:newline;
      const piece=chunk.subarray(offset,end);
      if(this.buffer.length+piece.length>MAX_FRAME){this.fail(new Error('Backend frame too large'));return;}
      this.buffer=Buffer.concat([this.buffer,piece]);
      if(newline<0)return;
      const frame=this.buffer;this.buffer=Buffer.alloc(0);offset=newline+1;
      try{this.handleFrame(new TextDecoder('utf-8',{fatal:true}).decode(frame));}catch(error){this.fail(error instanceof Error?error:new Error('Invalid backend frame'));return;}
    }
  }
  private handleFrame(source:string):void {
    const frame=requireValid<Record<string,unknown>>(parseStrictJson(source),envelope,'backend envelope');
    if(frame.session!==this.session)throw new Error('Backend session mismatch');
    if(frame.code==='Ok'?frame.error!==null:frame.payload!==null)throw new Error('Inconsistent backend result envelope');
    if(frame.kind==='parentProof'){
      if(frame.id!==null||frame.action!=='hello'||frame.code!=='Ok'||!this.proofResolve||!object(frame.payload))throw new Error('Unexpected parent proof');
      requireValid(frame.payload,shape({parentPid:integer,parentCreated:text(20),parentExecutable:text(),childPid:integer,childCreated:text(20),userSid:text(256),sessionId:integer,authorityMode}),'parent proof');
      const resolve=this.proofResolve;this.proofResolve=null;resolve(frame.payload);return;
    }
    if(frame.kind==='snapshot'){
      if(!this.ready||frame.id!==null||frame.action!=='snapshot'||frame.code!=='Ok')throw new Error('Invalid snapshot event');
      this.emit('snapshot',requireValid<ManagerSnapshot>(frame.payload,snapshot,'snapshot'));return;
    }
    if(typeof frame.id!=='string')throw new Error('Invalid backend response ID');
    const pending=this.pending.get(frame.id);
    if(!pending){
      const action=this.expired.get(frame.id);
      if(action){if(frame.action!==action)throw new Error('Expired backend response action mismatch');if(frame.code==='Ok')requireValid(frame.payload,results[action]!,'expired backend response');this.expired.delete(frame.id);return;}
      throw new Error('Unsolicited backend response');
    }
    if(pending.action!==frame.action)throw new Error('Backend response action mismatch');
    if(frame.code!=='Ok'){this.pending.delete(frame.id);clearTimeout(pending.timer);pending.reject(new Error(`Backend: ${String(frame.code)}${frame.error?` (${String(frame.error)})`:''}`));return;}
    const value=requireValid(frame.payload,results[pending.action]!,'backend response');
    this.pending.delete(frame.id);clearTimeout(pending.timer);pending.resolve(value);
  }
  private fail(error:Error):void {
    if(this.failure)return;
    this.failure=error;this.ready=false;this.proofReject?.(error);this.proofReject=null;
    for(const [id,pending] of this.pending){clearTimeout(pending.timer);pending.reject(new UnknownAfterDispatchError(pending.action,id));}
    this.pending.clear();this.child?.stdin.end();this.emit('unavailable',error);
  }
  async shutdown(timeoutMs=15000):Promise<OperationResult> {
    if(!this.child||!this.exitPromise)throw new Error('Backend never started');
    const deadline=Date.now()+timeoutMs;
    const result=await this.request<OperationResult>('shutdown',{},timeoutMs);
    this.child.stdin.end();
    const remaining=Math.max(1,deadline-Date.now());
    let timer:ReturnType<typeof setTimeout>|undefined;
    const exit=await Promise.race([this.exitPromise,new Promise<never>((_resolve,reject)=>{timer=setTimeout(()=>reject(new Error(t.backendShutdownUnknown(this.pid))),remaining);})]).finally(()=>clearTimeout(timer));
    if(exit.code!==0||result.outcome!=='Confirmed')throw new Error(`Backend shutdown not confirmed (${exit.code??exit.signal})`);
    return result;
  }
}
