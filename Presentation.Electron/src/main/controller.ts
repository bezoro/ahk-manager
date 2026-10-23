import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { ActivityQuery, ActivitySnapshot, CatalogResult, HostState, ImportPreview, ManagerSettings, ManagerSnapshot, OperationResult, ProcessIdentity, ScriptMetadata, ScriptOperation, ScriptSnapshot, SelectionContext, WindowPlacement } from '../shared/contracts.js';
import type { PreparedOperation, ProfileMigrationPreview } from './backend.js';
import { MAX_PENDING } from './backend.js';
import {hostText as t} from '../shared/host-pt-BR.js';
import {withinDeadline} from './lifecycle.js';

export interface StopExitItem {scriptId:string;name:string;path:string;pid:number|null;outcome:'alreadyStopped'|'confirmed'|'untouched'|'unknown';reason:string}
export interface StopExitReceipt {canExit:boolean;cancelled:boolean;expired:boolean;confirmedStops:number;items:StopExitItem[];problems:string[]}
export interface StopExitOptions {cancelled():boolean;now?():number;progress?:(completed:number,total:number)=>void;finalObservation?:(request:()=>Promise<ManagerSnapshot>,milliseconds:number)=>Promise<ManagerSnapshot>}
const nonblockingGlobal=new Set(['StartupInactive','StartupEntryConflict','ActivityUnavailable']);
const blockingScript=new Set(['ObservationIncomplete','PendingOperation','AccessDenied','MultipleInstances','IdentityChanged','WindowUnverified','CapabilityUnverified','EffectOwnerUnavailable','LegacyGuard','ReadOnlySettings','StorageFailure','MissingFile']);
function trustworthy(snapshot:ManagerSnapshot):boolean {return snapshot.effectsAvailable&&!snapshot.readOnly&&snapshot.observedAt!==null&&!snapshot.issues.some(issue=>!nonblockingGlobal.has(issue.code));}
function rowProblem(script:ScriptSnapshot):string|null {return script.issues.find(issue=>blockingScript.has(issue.code))?.code??(script.status==='Stopped'&&script.process===null?null:script.status==='Running'&&script.process?.windowVerified&&!script.runAsAdministrator?script.process.identity.scriptPath.toLowerCase()===script.path.toLowerCase()?null:'IdentityChanged':script.runAsAdministrator?'ManualElevationRequired':script.status);}
export function sameProcessIdentity(a:ProcessIdentity|null,b:ProcessIdentity|null):boolean {return !!a&&!!b&&a.pid===b.pid&&a.creationFileTime===b.creationFileTime&&a.executablePath.toLowerCase()===b.executablePath.toLowerCase()&&a.scriptPath.toLowerCase()===b.scriptPath.toLowerCase()&&a.userSid===b.userSid&&a.sessionId===b.sessionId&&a.scriptHash===b.scriptHash&&a.interpreterHash===b.interpreterHash;}

export interface Authority { request<T>(action:string,payload:unknown):Promise<T> }
export interface NativeBoundary {
  files(kind:'scripts'|'folder'|'interpreter'|'editor'|'import'|'migration'):Promise<string[]>;
  confirm(title:string,detail:string):Promise<boolean>;
  placement():WindowPlacement;
  alive():boolean;
}
const cancelled=():OperationResult=>({outcome:'CancelledBeforeDispatch',code:'Cancelled',detail:null,correlationId:null});
const stale=():OperationResult=>({outcome:'CancelledBeforeDispatch',code:'StaleSelection',detail:null,correlationId:null});
export function selectionKey(selection:SelectionContext|null):string {
  return selection?JSON.stringify([selection.scriptId,selection.query.search,selection.query.status,selection.query.sort,selection.query.collectionId,selection.query.version]):'';
}
export function selectionVisible(snapshot:ManagerSnapshot,selection:SelectionContext):boolean {
  const script=snapshot.scripts.find(s=>s.id===selection.scriptId);if(!script)return false;
  const query=selection.query;
  if(query.collectionId!==null&&script.collectionId!==query.collectionId)return false;
  if(query.status==='Running'&&script.status!=='Running')return false;
  if(query.status==='Stopped'&&script.status!=='Stopped')return false;
  if(query.status==='Problems'&&!script.issues.length&&!['Unknown','Multiple','Missing','Blocked'].includes(script.status))return false;
  const search=query.search.trim().toLocaleLowerCase('pt-BR');
  return !search||`${script.friendlyName}\n${script.path}\n${script.description}`.toLocaleLowerCase('pt-BR').includes(search);
}
export class ManagerController {
  state:HostState={snapshot:null,connection:'starting',error:null,busy:false};
  private tail:Promise<unknown>=Promise.resolve();
  private queued=0;
  private selection:SelectionContext|null=null;
  private selectionEpoch=0;
  private paths=new Set<string>();
  private previews=new Map<string,{previewId:string;preview:ImportPreview;expires:number}>();
  private stopExitActive=false;
  private stopExitVersion=0n;
  constructor(private authority:Authority,private readonly native:NativeBoundary,private readonly publish:(state:HostState)=>void){}
  setAuthority(authority:Authority):void {this.authority=authority;this.paths.clear();this.previews.clear();this.updateSelection(null);}
  setSnapshot(snapshot:ManagerSnapshot):void {this.state={...this.state,snapshot};this.publish(this.state);}
  setConnection(connection:HostState['connection'],error:string|null=null):void {this.state={...this.state,connection,error};if(connection!=='ready'){this.previews.clear();this.updateSelection(null);}this.publish(this.state);}
  updateSelection(value:SelectionContext|null):void {if(selectionKey(value)!==selectionKey(this.selection))this.selectionEpoch++;this.selection=value;}
  private requireReady():ManagerSnapshot {if(this.state.connection!=='ready'||!this.state.snapshot)throw new Error(t.backendUnavailable);return this.state.snapshot;}
  private matches(revision:string):boolean {return this.requireReady().revision===revision;}
  private async serialize<T>(work:()=>Promise<T>):Promise<T> {
    this.requireReady();if(this.stopExitActive)throw new Error(t.stopExitBusy);if(this.queued>=MAX_PENDING)throw new Error(t.queueFull);
    this.queued++;this.state={...this.state,busy:true};this.publish(this.state);
    const task=this.tail.then(()=>{this.requireReady();if(!this.native.alive())throw new Error(t.documentClosed);return work();});
    this.tail=task.catch(()=>undefined);
    return task.finally(()=>{this.queued--;this.state={...this.state,busy:this.queued>0||this.stopExitActive};this.publish(this.state);});
  }
  async refresh():Promise<void> {this.requireReady();this.setSnapshot(await this.authority.request<ManagerSnapshot>('refresh',{}));}
  async runLogin():Promise<OperationResult> {return this.serialize(()=>this.authority.request('runLogin',{}));}
  async add(kind:'scripts'|'folder'):Promise<CatalogResult|null> {return this.serialize(async()=>{const paths=await this.native.files(kind);if(!paths.length||!this.native.alive())return null;return kind==='scripts'?this.authority.request('addFiles',{paths}):this.authority.request('addFolder',{path:paths[0]});});}
  async remove(scriptId:string,revision:string):Promise<OperationResult> {return this.serialize(async()=>{
    if(!this.matches(revision))return stale();const script=this.state.snapshot!.scripts.find(s=>s.id===scriptId);if(!script)return stale();
    if(!await this.native.confirm(t.removeTitle,t.removeDetail(script.friendlyName,script.path)))return cancelled();
    if(!this.native.alive()||!this.matches(revision))return stale();return this.authority.request('remove',{scriptId});
  });}
  async saveMetadata(scriptId:string,metadata:ScriptMetadata,revision:string):Promise<OperationResult> {return this.serialize(async()=>{
    if(!this.matches(revision))return stale();const script=this.state.snapshot!.scripts.find(s=>s.id===scriptId);if(!script)return stale();
    let startupConsent=false;
    if(metadata.runAtLogin===true&&script.runAtLogin!==true){startupConsent=await this.native.confirm(t.scriptStartupTitle,t.scriptStartupDetail(script.friendlyName,script.path));if(!startupConsent)return cancelled();}
    if(!this.native.alive()||!this.matches(revision))return stale();return this.authority.request('saveMetadata',{scriptId,metadata,startupConsent});
  });}
  async saveCollection(collectionId:string|null,name:string):Promise<OperationResult> {return this.serialize(()=>this.authority.request('saveCollection',{collectionId,name}));}
  async removeCollection(collectionId:string,revision:string):Promise<OperationResult> {return this.serialize(async()=>{
    if(!this.matches(revision))return stale();const collection=this.state.snapshot!.collections.find(c=>c.id===collectionId);if(!collection)return stale();
    if(!await this.native.confirm(t.removeCollectionTitle,t.removeCollectionDetail(collection.name)))return cancelled();
    if(!this.native.alive()||!this.matches(revision))return stale();return this.authority.request('removeCollection',{collectionId});
  });}
  async operate(operation:ScriptOperation,selection:SelectionContext):Promise<OperationResult> {
    const requestedEpoch=this.selectionEpoch;
    return this.serialize(async()=>{
      const valid=()=>this.native.alive()&&this.selectionEpoch===requestedEpoch&&selectionKey(selection)===selectionKey(this.selection)&&selectionVisible(this.requireReady(),selection);
      if(!valid()||!this.matches(selection.stateRevision))return stale();
      const prepared=await this.authority.request<PreparedOperation>('prepare',{selection,operation});
      if(!prepared.plan.allowed)return {outcome:'Failed',code:prepared.plan.reason,detail:null,correlationId:prepared.plan.correlationId};
      if(!valid())return stale();
      const target=this.state.snapshot!.scripts.find(s=>s.id===selection.scriptId)!;
      let confirmed=false;
      if(prepared.plan.requiresConfirmation){confirmed=await this.native.confirm(t.operationTitle,t.operationDetail(t.operations[operation],target.friendlyName,target.path,target.process?.identity.pid??null));if(!confirmed)return cancelled();}
      if(!valid())return stale();
      // A later authoritative revision is supplied; backend rejects a stale plan itself.
      const currentSelection={...selection,stateRevision:this.state.snapshot!.revision};
      return this.authority.request('perform',{planId:prepared.planId,currentSelection,confirmed});
    });
  }
  async pick(kind:'interpreter'|'editor'):Promise<string|null> {if(this.stopExitActive)throw new Error(t.stopExitBusy);const values=await this.native.files(kind);const value=values[0]??null;if(value&&this.native.alive())this.paths.add(path.normalize(value).toLowerCase());return this.native.alive()?value:null;}
  async saveSettings(proposed:ManagerSettings):Promise<OperationResult> {return this.serialize(async()=>{
    const current=this.requireReady();
    for(const key of ['interpreterPath','editorPath'] as const){const value=proposed[key];if(value!==null&&value!==current.settings[key]&&!this.paths.has(path.normalize(value).toLowerCase()))throw new Error(t.selectExecutable);}
    let startupConsent=false;
    if(proposed.launchManagerAtLogin&&!current.settings.launchManagerAtLogin){startupConsent=await this.native.confirm(t.managerStartupTitle,t.managerStartupDetail);if(!startupConsent)return cancelled();}
    if(!this.native.alive()||!this.matches(current.revision))return stale();
    return this.authority.request('saveSettings',{settings:{...proposed,window:this.native.placement()},startupConsent});
  });}
  async activity(query:ActivityQuery):Promise<ActivitySnapshot[]> {this.requireReady();return this.authority.request('activity',{query});}
  async previewImport():Promise<{token:string;preview:ImportPreview}|null> {return this.serialize(async()=>{
    const paths=await this.native.files('import');if(!paths.length||!this.native.alive())return null;
    const result=await this.authority.request<{previewId:string;preview:ImportPreview}>('previewImport',{sourcePath:paths[0]});
    for(const [key,value] of this.previews)if(value.expires<Date.now())this.previews.delete(key);
    if(this.previews.size>=32)throw new Error(t.previewsFull);
    const token=randomUUID();this.previews.set(token,{...result,expires:Date.now()+300000});return {token,preview:result.preview};
  });}
  async commitImport(token:string):Promise<OperationResult> {return this.serialize(async()=>{
    const held=this.previews.get(token);this.previews.delete(token);if(!held||held.expires<Date.now()||!held.preview.canImport)return stale();
    if(!await this.native.confirm(t.importTitle,t.importDetail(held.preview.sourcePath,held.preview.scriptCount)))return cancelled();
    if(!this.native.alive())return stale();return this.authority.request('import',{previewId:held.previewId});
  });}
  async migrateProfile(beforeLoad=false):Promise<OperationResult|null> {
    const migrate=async():Promise<OperationResult|null>=>{
      const paths=await this.native.files('migration');if(!paths.length||!this.native.alive())return null;
      const preview=await this.authority.request<ProfileMigrationPreview>('previewMigration',{sourceRoot:paths[0]});
      if(!preview.canMigrate)return {outcome:'Failed',code:preview.issues[0]?.code??'ImportInvalid',detail:preview.sourceRoot,correlationId:null};
      const confirmed=await this.native.confirm(t.migrationTitle,t.migrationDetail(preview.sourceRoot,preview.fileCount,preview.pendingOperations));
      if(!confirmed)return cancelled();if(!this.native.alive())return stale();return this.authority.request('migrate',{previewId:preview.previewId,confirmed:true});
    };
    return beforeLoad?migrate():this.serialize(migrate);
  }
  async transferStartup():Promise<OperationResult> {return this.serialize(async()=>{
    const preview=await this.authority.request<{previewId:string;oldExecutable:string|null;newExecutable:string;oldCommand:string|null;newCommand:string;canTransfer:boolean;reason:string}>('previewStartupTransfer',{});
    if(!preview.canTransfer)return {outcome:'Failed',code:preview.reason,detail:preview.oldExecutable,correlationId:null};
    const confirmed=await this.native.confirm(t.transferTitle,t.transferDetail(preview.oldExecutable,preview.newExecutable));
    if(!this.native.alive())return stale();return this.authority.request('transferStartup',{previewId:preview.previewId,confirmed});
  });}
  async restoreBackup():Promise<OperationResult> {return this.serialize(async()=>{if(!await this.native.confirm(t.restoreTitle,t.restoreDetail))return cancelled();if(!this.native.alive())return stale();return this.authority.request('restoreBackup',{});});}
  async recoverPending():Promise<OperationResult> {return this.serialize(()=>this.authority.request('recoverPending',{}));}
  async savePlacement():Promise<void> {
    if(!this.state.snapshot||!['ready','closing'].includes(this.state.connection))return;
    const save=async()=>{if(this.state.snapshot)await this.authority.request('saveSettings',{settings:{...this.state.snapshot.settings,window:this.native.placement()},startupConsent:false});};
    if(this.state.connection==='closing')await save();else await this.serialize(save);
  }
  async drain():Promise<void> {await this.tail;}

  /** Native host only: never called by renderer IPC and independent of its selected row. */
  async stopScriptsForExit(options:StopExitOptions):Promise<StopExitReceipt> {
    const receipt:StopExitReceipt={canExit:false,cancelled:false,expired:false,confirmedStops:0,items:[],problems:[]};
    if(this.stopExitActive){receipt.problems.push('BatchBusy');return receipt;}
    this.stopExitActive=true;this.state={...this.state,busy:true};this.publish(this.state);
    const now=options.now??(()=>performance.now());
    const cancelled=()=>{if(options.cancelled()||!this.native.alive()){receipt.cancelled=true;return true;}return false;};
    const observe=async()=>{this.requireReady();const fresh=await this.authority.request<ManagerSnapshot>('refresh',{});this.requireReady();this.setSnapshot(fresh);return fresh;};
    try{
      // Prior work, initial observation and native consent do not spend the dispatch budget.
      await this.drain();if(cancelled())return receipt;
      const initial=await observe();if(cancelled())return receipt;
      if(!trustworthy(initial)){receipt.problems.push('ObservationUntrusted');return receipt;}
      const cohort=initial.scripts.map(script=>({id:script.id,path:script.path,name:script.friendlyName,identity:script.process?{...script.process.identity}:null}));
      const sameScope=(state:ManagerSnapshot)=>state.scripts.length===cohort.length&&cohort.every(original=>state.scripts.some(script=>script.id===original.id&&script.path.toLowerCase()===original.path.toLowerCase()&&script.friendlyName===original.name));
      const approved:typeof cohort=[];
      for(const script of initial.scripts){
        const problem=rowProblem(script);
        receipt.items.push({scriptId:script.id,name:script.friendlyName,path:script.path,pid:script.process?.identity.pid??null,outcome:problem?'untouched':script.status==='Stopped'?'alreadyStopped':'untouched',reason:problem??(script.status==='Stopped'?'None':'NotDispatched')});
        if(!problem&&script.status==='Running')approved.push(cohort.find(item=>item.id===script.id)!);
      }
      let incomplete=receipt.items.some(item=>item.reason!=='None'&&item.reason!=='NotDispatched');
      options.progress?.(0,approved.length);
      if(approved.length){
        const detail=t.stopExitConsent(receipt.items);
        if(detail.length>64000){receipt.problems.push('ConfirmationTooLarge');return receipt;}
        if(!await this.native.confirm(t.stopExitTitle,detail)){receipt.cancelled=true;return receipt;}
        if(cancelled())return receipt;
      }
      const deadline=now()+60000,version=(++this.stopExitVersion).toString();
      let completed=0;
      for(const original of approved){
        const item=receipt.items.find(row=>row.scriptId===original.id)!;
        if(cancelled())break;
        if(now()>=deadline){receipt.expired=true;break;}
        const fresh=await observe();
        if(cancelled())break;
        if(!trustworthy(fresh)||!sameScope(fresh)){incomplete=true;receipt.problems.push('ScopeOrAuthorityChanged');break;}
        if(now()>=deadline){receipt.expired=true;break;}
        const target=fresh.scripts.find(script=>script.id===original.id)!;
        if(!rowProblem(target)&&target.status==='Stopped'&&target.process===null){item.outcome='alreadyStopped';item.reason='None';options.progress?.(++completed,approved.length);continue;}
        if(rowProblem(target)||!sameProcessIdentity(original.identity,target.process?.identity??null)){incomplete=true;item.reason=rowProblem(target)??'IdentityChanged';options.progress?.(++completed,approved.length);continue;}
        const selection:SelectionContext={scriptId:original.id,stateRevision:fresh.revision,query:{search:'',status:'All',sort:'Name',collectionId:null,version}};
        const prepared=await this.authority.request<PreparedOperation>('prepare',{selection,operation:'Stop'});
        // Only known Stop tokens requiring confirmation are safe to discard this way.
        if(prepared.plan.operation!=='Stop'||!prepared.plan.requiresConfirmation){incomplete=true;item.reason='UnexpectedPlan';break;}
        const discard=async()=>{try{await this.authority.request('perform',{planId:prepared.planId,currentSelection:prepared.plan.selection,confirmed:false});}catch{incomplete=true;receipt.problems.push('DiscardUnconfirmed');}};
        if(cancelled()){await discard();break;}
        if(deadline-now()<30000){receipt.expired=true;await discard();break;}
        if(this.state.connection!=='ready'||!this.state.snapshot){incomplete=true;item.reason='EffectOwnerUnavailable';await discard();break;}
        const current=this.requireReady();
        const matches=prepared.plan.allowed&&prepared.plan.scriptId===original.id&&prepared.plan.stateRevision===fresh.revision&&selectionKey(prepared.plan.selection)===selectionKey(selection)&&prepared.plan.selection.stateRevision===selection.stateRevision&&sameProcessIdentity(original.identity,prepared.plan.expectedIdentity)&&trustworthy(current)&&sameScope(current)&&current.revision===fresh.revision&&sameProcessIdentity(original.identity,current.scripts.find(script=>script.id===original.id)?.process?.identity??null);
        if(!matches){incomplete=true;item.reason=prepared.plan.allowed?'StaleSelection':prepared.plan.reason;await discard();options.progress?.(++completed,approved.length);continue;}
        try{
          const result=await this.authority.request<OperationResult>('perform',{planId:prepared.planId,currentSelection:selection,confirmed:true});
          if(result.outcome==='Confirmed'&&result.code==='None'){item.outcome='confirmed';item.reason='None';receipt.confirmedStops++;}
          else{incomplete=true;item.outcome='unknown';item.reason=result.code==='None'?result.outcome:result.code;}
        }catch(error){incomplete=true;item.outcome='unknown';item.reason=error instanceof Error?error.message:'Unknown';}
        options.progress?.(++completed,approved.length);
        if(now()>deadline){receipt.expired=true;break;}
        if(cancelled())break;
      }
      // Cancellation/expiry/uncertain effects are sticky even if late observation looks stopped.
      const final=await (options.finalObservation??withinDeadline)(observe,30000);
      if(cancelled())incomplete=true;
      const allStopped=trustworthy(final)&&sameScope(final)&&final.scripts.every(script=>script.status==='Stopped'&&script.process===null&&!rowProblem(script));
      if(!allStopped)receipt.problems.push('FinalStateNotStopped');
      receipt.canExit=allStopped&&!incomplete&&!receipt.cancelled&&!receipt.expired&&receipt.items.every(item=>item.outcome==='confirmed'||item.outcome==='alreadyStopped');
      return receipt;
    }catch(error){receipt.problems.push(error instanceof Error?error.message:'Unknown');return receipt;}
    finally{this.stopExitActive=false;this.state={...this.state,busy:this.queued>0};this.publish(this.state);}
  }
}
