import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { ActivityQuery, ActivitySnapshot, CatalogResult, HostState, ImportPreview, ManagerSettings, ManagerSnapshot, OperationResult, ScriptMetadata, ScriptOperation, SelectionContext, WindowPlacement } from '../shared/contracts.js';
import type { PreparedOperation, ProfileMigrationPreview } from './backend.js';
import { MAX_PENDING } from './backend.js';
import {hostText as t} from '../shared/host-pt-BR.js';

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
  constructor(private authority:Authority,private readonly native:NativeBoundary,private readonly publish:(state:HostState)=>void){}
  setAuthority(authority:Authority):void {this.authority=authority;this.paths.clear();this.previews.clear();this.updateSelection(null);}
  setSnapshot(snapshot:ManagerSnapshot):void {this.state={...this.state,snapshot};this.publish(this.state);}
  setConnection(connection:HostState['connection'],error:string|null=null):void {this.state={...this.state,connection,error};if(connection!=='ready'){this.previews.clear();this.updateSelection(null);}this.publish(this.state);}
  updateSelection(value:SelectionContext|null):void {if(selectionKey(value)!==selectionKey(this.selection))this.selectionEpoch++;this.selection=value;}
  private requireReady():ManagerSnapshot {if(this.state.connection!=='ready'||!this.state.snapshot)throw new Error(t.backendUnavailable);return this.state.snapshot;}
  private matches(revision:string):boolean {return this.requireReady().revision===revision;}
  private async serialize<T>(work:()=>Promise<T>):Promise<T> {
    this.requireReady();if(this.queued>=MAX_PENDING)throw new Error(t.queueFull);
    this.queued++;this.state={...this.state,busy:true};this.publish(this.state);
    const task=this.tail.then(()=>{this.requireReady();if(!this.native.alive())throw new Error(t.documentClosed);return work();});
    this.tail=task.catch(()=>undefined);
    return task.finally(()=>{this.queued--;this.state={...this.state,busy:this.queued>0};this.publish(this.state);});
  }
  async refresh():Promise<void> {this.requireReady();this.setSnapshot(await this.authority.request<ManagerSnapshot>('refresh',{}));}
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
  async pick(kind:'interpreter'|'editor'):Promise<string|null> {const values=await this.native.files(kind);const value=values[0]??null;if(value&&this.native.alive())this.paths.add(path.normalize(value).toLowerCase());return this.native.alive()?value:null;}
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
}
