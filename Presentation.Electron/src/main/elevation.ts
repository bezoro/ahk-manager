import { randomUUID } from 'node:crypto';
import type { AdministrationUiGuard, ElevationStatus, OperationResult } from '../shared/contracts.js';

export type AdministrationPhase = 'begin' | 'commit';
export interface PreflightRequest { operationId:string; phase:AdministrationPhase; nonce:string }
/** The transport must authenticate the document before delivering replies here. */
export class AdministrationPreflight {
  private registered=false;
  private pending:{request:PreflightRequest;valid:()=>boolean;resolve:(value:boolean)=>void;timer:ReturnType<typeof setTimeout>}|null=null;
  register(value:boolean):void {this.registered=value;if(!value)this.invalidate();}
  invalidate():void {this.registered=false;this.finish(false);}
  private finish(value:boolean):void {const pending=this.pending;this.pending=null;if(pending){clearTimeout(pending.timer);pending.resolve(value&&pending.valid());}}
  reply(request:PreflightRequest,value:AdministrationUiGuard|null):void {
    const pending=this.pending;
    if(!pending||request.operationId!==pending.request.operationId||request.phase!==pending.request.phase||request.nonce!==pending.request.nonce)return;
    this.finish(value!==null&&!value.dirtyDrafts&&!value.dialogOpen&&!value.pendingWork);
  }
  async check(operationId:string,phase:AdministrationPhase,send:(request:PreflightRequest)=>void,valid:()=>boolean,milliseconds=3000):Promise<boolean> {
    const deadline=Date.now()+milliseconds;
    while(!this.registered&&valid()&&Date.now()<deadline)await new Promise(resolve=>setTimeout(resolve,25));
    if(!this.registered||!valid()||this.pending)return false;
    return new Promise(resolve=>{
      const request={operationId,phase,nonce:randomUUID()};
      this.pending={request,valid,resolve,timer:setTimeout(()=>this.finish(false),Math.max(1,deadline-Date.now()))};
      try{send(request);}catch{this.finish(false);}
    });
  }
}

export interface ElevationBoundary {
  eligible():boolean;
  confirm():Promise<boolean>;
  preflight(operationId:string,phase:AdministrationPhase):Promise<boolean>;
  quiesce(value:boolean):void;
  drain():Promise<void>;
  persistPlacement():Promise<void>;
  request(action:string,payload:unknown,timeoutMs?:number):Promise<ElevationStatus>;
  quitAfterAcknowledgement():Promise<void>;
  now?():number;
}
const result=(outcome:OperationResult['outcome'],code:string,detail:string|null=null):OperationResult=>({outcome,code,detail,correlationId:null});
/** One ticket owns the mutation lane until an authenticated resume or commit receipt. */
export class ElevationCoordinator {
  private ticket:{id:string;revision:string;commitSent:boolean;commitDeadline:number|null;acknowledged:boolean}|null=null;
  private running=false;
  constructor(private readonly boundary:ElevationBoundary){}
  get active():boolean{return this.running||this.ticket!==null;}
  get processing():boolean{return this.running;}
  async request(explicitCli=false):Promise<OperationResult> {
    if(this.running)return result('CancelledBeforeDispatch','PendingOperation');
    this.running=true;
    try{
      if(!this.ticket){
        if(!this.boundary.eligible())return result('CancelledBeforeDispatch','CapabilityUnverified');
        const id=randomUUID();
        if(!await this.boundary.preflight(id,'begin'))return result('CancelledBeforeDispatch','PendingOperation');
        if(!explicitCli&&!await this.boundary.confirm())return result('CancelledBeforeDispatch','Cancelled');
        if(!this.boundary.eligible())return result('CancelledBeforeDispatch','CapabilityUnverified');
        this.boundary.quiesce(true);
        try{
          await this.boundary.drain();await this.boundary.persistPlacement();
          if(!await this.boundary.preflight(id,'begin')){this.boundary.quiesce(false);return result('CancelledBeforeDispatch','PendingOperation');}
        }catch{this.boundary.quiesce(false);return result('Failed','StorageFailure');}
        this.ticket={id,revision:'0',commitSent:false,commitDeadline:null,acknowledged:false};
        let status:ElevationStatus|null=null;
        try{status=await this.boundary.request('beginAdministration',{operationId:id});}catch{/* A sent mutation is never replayed. */}
        return await this.observe(status);
      }
      if(this.ticket.acknowledged){await this.boundary.quitAfterAcknowledgement();return result('Confirmed','None');}
      return await this.observe(null);
    }finally{this.running=false;}
  }
  private async observe(initial:ElevationStatus|null):Promise<OperationResult> {
    const ticket=this.ticket!;const now=this.boundary.now??Date.now;const deadline=now()+120000;
    let status=initial;
    while(now()<deadline){
      if(!status){try{status=await this.boundary.request('administrationStatus',{operationId:ticket.id,afterRevision:ticket.revision},ticket.commitDeadline===null?30000:Math.max(1,Math.min(15000,ticket.commitDeadline-now())));}catch{return result('Unknown','PendingOperation');}}
      if(status.operationId!==ticket.id||BigInt(status.revision)<BigInt(ticket.revision))return result('Unknown','IdentityChanged');
      ticket.revision=status.revision;
      if(status.state==='CommitAcked'&&status.receiptId){ticket.acknowledged=true;await this.boundary.quitAfterAcknowledgement();return result('Confirmed','None');}
      if(status.canResumeNormal){this.ticket=null;this.boundary.quiesce(false);return result(status.state==='Cancelled'||status.state==='Denied'?'CancelledBeforeDispatch':'Failed',status.reason??'Cancelled');}
      if(status.state==='Ready'&&!ticket.commitSent){
        if(!await this.boundary.preflight(ticket.id,'commit')){
          try{status=await this.boundary.request('cancelAdministration',{operationId:ticket.id});}catch{return result('Unknown','PendingOperation');}
          // Cancellation must be authenticated; Ready cannot accidentally send Commit.
          ticket.commitSent=true;continue;
        }
        ticket.commitSent=true;
        ticket.commitDeadline=now()+15000;
        try{status=await this.boundary.request('commitAdministration',{operationId:ticket.id},15000);}catch{status=null;}
        continue;
      }
      if(['Unknown','Expired','Failed','Cancelled','Denied','Completed'].includes(status.state))return result('Unknown',status.reason??'PendingOperation');
      if(ticket.commitDeadline!==null&&now()>=ticket.commitDeadline)return result('Unknown','PendingOperation');
      status=null;
    }
    return result('Unknown','PendingOperation');
  }
}
