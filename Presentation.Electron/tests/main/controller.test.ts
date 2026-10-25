import test from 'node:test';
import assert from 'node:assert/strict';
import { ManagerController, selectionVisible, type NativeBoundary } from '../../src/main/controller.js';
import { snapshot, selection, scriptId, planId, runningSnapshot } from './fixtures.js';
import type { ManagerSnapshot, OperationPlan, OperationResult, ProcessIdentity } from '../../src/shared/contracts.js';
import { metadataOf } from '../../src/renderer/model.js';
const confirmed:OperationResult={outcome:'Confirmed',code:'None',detail:null,correlationId:null};
test('locate native cancellation/stale selection never mutates the catalog',async()=>{
  const {controller,native,requests}=setup();const state=snapshot();state.scripts[0]!.status='Missing';controller.setSnapshot(state);
  assert.equal((await controller.locate(selection())).outcome,'CancelledBeforeDispatch');assert.equal(requests.length,0);
  native.files=async()=>{controller.updateSelection(null);return ['C:\\fixtures\\replacement.ahk'];};
  assert.equal((await controller.locate(selection())).code,'StaleSelection');assert.equal(requests.length,0);
});
test('locate sends native-selected path only with unchanged identity/revision and separate startup consent',async()=>{
  const {controller,native,requests}=setup();const state=snapshot();state.scripts[0]!.status='Missing';state.scripts[0]!.runAtLogin=true;controller.setSnapshot(state);
  native.files=async(kind)=>{assert.equal(kind,'locate');return ['C:\\fixtures\\replacement.ahk'];};
  native.confirm=async()=>false;assert.equal((await controller.locate(selection())).outcome,'CancelledBeforeDispatch');assert.equal(requests.length,0);
  native.confirm=async()=>true;await controller.locate(selection());assert.deepEqual(requests.map(r=>r.action),['relink']);assert.equal(requests[0]!.payload.oldPath,state.scripts[0]!.path);assert.equal(requests[0]!.payload.startupConsent,true);
});
test('delete exact consent precedes backend stop authorization and native trash, then reports same operation receipt',async()=>{
  const {controller,native,requests,authority}=setup();const events:string[]=[];const file={path:snapshot().scripts[0]!.path,fileId:'windows-id',hash:'a'.repeat(64)};
  authority.request=async<T>(action:string,payload:any):Promise<T>=>{requests.push({action,payload});events.push(action);if(action==='prepareRecycle')return {operationId:planId,scriptId,path:file.path,file,expectedIdentity:null,selection:selection(),allowed:true,reason:'None'} as T;if(action==='authorizeRecycle')return {result:{...confirmed,outcome:'Pending'},file,stopped:true} as T;return confirmed as T;};
  native.fileStamp=async()=>{events.push('stamp');return 'same';};native.confirm=async(_title,detail)=>{assert.ok(detail.includes(file.path)&&detail.includes('Lixeira')&&detail.includes('administrador'));events.push('confirm');return true;};native.trash=async target=>{assert.equal(target,file.path);events.push('trash');};
  assert.equal((await controller.deleteFile(selection())).outcome,'Confirmed');
  assert.deepEqual(events,['prepareRecycle','stamp','confirm','authorizeRecycle','stamp','trash','completeRecycle']);assert.equal(requests.at(-1)!.payload.receipt,'Confirmed');assert.equal(requests.at(-1)!.payload.operationId,planId);
});
test('delete cancellation, host stamp change and native rejection never claim a confirmed recycle',async()=>{
  for(const scenario of ['cancel','stamp','denied','uncertain-denial']){
    const {controller,native,requests,authority}=setup();let stamps=0,trash=0;const file={path:snapshot().scripts[0]!.path,fileId:'windows-id',hash:'a'.repeat(64)};
    authority.request=async<T>(action:string,payload:any):Promise<T>=>{requests.push({action,payload});if(action==='prepareRecycle')return {operationId:planId,scriptId,path:file.path,file,expectedIdentity:null,selection:selection(),allowed:true,reason:'None'} as T;if(action==='authorizeRecycle')return {result:{...confirmed,outcome:'Pending'},file,stopped:true} as T;return {...confirmed,outcome:'Partial'} as T;};
    native.confirm=async()=>scenario!=='cancel';native.fileStamp=async()=>{stamps++;if(scenario==='uncertain-denial'&&stamps>2)throw new Error('File preservation unobservable');return scenario==='stamp'?String(stamps):'same';};native.trash=async()=>{trash++;throw new Error('permission denied');};
    const result=await controller.deleteFile(selection());assert.notEqual(result.outcome,'Confirmed');assert.equal(trash,scenario==='denied'||scenario==='uncertain-denial'?1:0);assert.equal(requests.some(r=>r.action==='completeRecycle'&&r.payload.receipt==='Confirmed'),false);assert.equal(requests.some(r=>r.action==='authorizeRecycle'),scenario!=='cancel');if(scenario==='uncertain-denial')assert.equal(requests.at(-1)!.payload.receipt,'Unknown');
  }
});
test('native deadline is Unknown and same operation receives one late affirmative receipt without dispatch replay',async context=>{
  context.mock.timers.enable({apis:['setTimeout']});
  const {controller,native,requests,authority}=setup();let trash=0;let finish!:()=>void;const pending=new Promise<void>(resolve=>{finish=resolve;});const file={path:snapshot().scripts[0]!.path,fileId:'windows-id',hash:'a'.repeat(64)};
  authority.request=async<T>(action:string,payload:any):Promise<T>=>{requests.push({action,payload});if(action==='prepareRecycle')return {operationId:planId,scriptId,path:file.path,file,expectedIdentity:null,selection:selection(),allowed:true,reason:'None'} as T;if(action==='authorizeRecycle')return {result:{...confirmed,outcome:'Pending'},file,stopped:true} as T;return confirmed as T;};
  native.fileStamp=async()=> 'same';native.trash=async()=>{trash++;await pending;};
  const result=controller.deleteFile(selection());for(let i=0;i<20&&trash===0;i++)await new Promise(resolve=>setImmediate(resolve));assert.equal(trash,1);
  context.mock.timers.tick(30001);assert.equal((await result).outcome,'Unknown');assert.equal(requests.at(-1)!.payload.receipt,'Unknown');
  controller.updateSelection(null);finish();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(trash,1);assert.deepEqual(requests.filter(r=>r.action==='completeRecycle').map(r=>[r.payload.operationId,r.payload.receipt]),[[planId,'Unknown'],[planId,'Confirmed']]);
});
function setup(confirm:NativeBoundary['confirm']=async()=>true){
  const requests:{action:string;payload:any}[]=[];
  const native:NativeBoundary={confirm,files:async()=>[],alive:()=>true,placement:()=>snapshot().settings.window};
  const authority={request:async<T>(action:string,payload:any):Promise<T>=>{
    requests.push({action,payload});
    if(action==='prepare')return {planId,plan:{correlationId:planId,scriptId,operation:'Stop',stateRevision:selection().stateRevision,queryVersion:selection().query.version,expectedIdentity:null,allowed:true,requiresConfirmation:true,reason:'None',selection:selection()}} as T;
    if(action==='refresh')return snapshot() as T;
    return confirmed as T;
  }};
  const controller=new ManagerController(authority,native,()=>{});controller.setSnapshot(snapshot());controller.setConnection('ready');controller.updateSelection(selection());
  return {controller,requests,native,authority};
}
function reviewRecycle(filter:'All'|'Running'|'Stopped'='All'){
  const held=setup();const state=snapshot();state.revision='1';const selected={...selection(),stateRevision:'1',query:{...selection().query,status:filter}};
  if(filter==='Running'){state.scripts[0]!.status='Running';const process=runningSnapshot().scripts[0]!.process!;state.scripts[0]!.process={...process,identity:{...process.identity,scriptPath:state.scripts[0]!.path}};}
  held.controller.setSnapshot(state);held.controller.updateSelection(selected);
  const file={path:state.scripts[0]!.path,fileId:'windows-id',hash:'a'.repeat(64)};let trash=0;
  held.native.fileStamp=async()=> 'same';held.native.trash=async()=>{trash++;};
  held.authority.request=async<T>(action:string,payload:any):Promise<T>=>{
    held.requests.push({action,payload});
    if(action==='prepareRecycle')return {operationId:planId,scriptId,path:file.path,file,expectedIdentity:state.scripts[0]!.process?.identity??null,selection:selected,allowed:true,reason:'None'} as T;
    if(action==='authorizeRecycle'){
      held.controller.setSnapshot({...state,revision:'2',scripts:[{...state.scripts[0]!,status:'Unknown',process:null,issues:[{code:'PendingOperation',detail:null,scriptId}]}]});
      return {result:{...confirmed,outcome:'Pending'},file,stopped:true} as T;
    }
    return {...confirmed,...(payload.receipt==='Failed'?{outcome:'Partial',detail:'Execução encerrada.'}: {})} as T;
  };
  return {...held,selected,get trash(){return trash;}};
}
for(const invalidate of ['selection','document','authority','readonly','disconnect','path','process','unknown'])test('review: final asynchronous stamp cannot outlive '+invalidate+' consent',async()=>{
    const fixture=reviewRecycle();let release!:(value:string)=>void,stamps=0;const stamp=new Promise<string>(resolve=>{release=resolve;});
    fixture.native.fileStamp=async()=>++stamps===2?stamp:'same';const result=fixture.controller.deleteFile(fixture.selected);
    for(let i=0;i<20&&stamps<2;i++)await new Promise(resolve=>setImmediate(resolve));assert.equal(stamps,2);
    if(invalidate==='selection')fixture.controller.updateSelection(null);else if(invalidate==='document')fixture.native.alive=()=>false;else if(invalidate==='authority')fixture.controller.setAuthority({request:async<T>()=>confirmed as T});else if(invalidate==='disconnect')fixture.controller.setConnection('failed');else{
      const state=fixture.controller.state.snapshot!;const row=state.scripts[0]!;let updated={...row};
      if(invalidate==='path')updated.path='C:\\fixtures\\different.ahk';if(invalidate==='unknown')updated.issues=[];
      if(invalidate==='process'){const process=runningSnapshot().scripts[0]!.process!;updated={...updated,status:'Running',process:{...process,identity:{...process.identity,pid:9999,scriptPath:row.path}}};}
      fixture.controller.setSnapshot({...state,readOnly:invalidate==='readonly',scripts:[updated]});
    }
    release('same');const completed=await result;
    assert.equal(fixture.trash,0,'Trash escaped invalidated '+invalidate);assert.equal(completed.outcome,'Partial');assert.equal(completed.detail,'Execução encerrada.');if(invalidate!=='process')assert.equal(fixture.controller.state.snapshot!.scripts[0]!.process,null);
});
for(const filter of ['Running','Stopped'] as const)test('review: own Pending snapshot must not veto confirmed deletion under '+filter+' filter',async()=>{
  const fixture=reviewRecycle(filter);const result=await fixture.controller.deleteFile(fixture.selected);assert.equal(fixture.trash,1,'Own Pending snapshot cancelled '+filter);assert.equal(result.outcome,'Confirmed');assert.equal(fixture.requests.at(-1)!.payload.receipt,'Confirmed');
});
test('review: native deadline resolves UI Unknown even if background completion reporting never settles',async context=>{
  context.mock.timers.enable({apis:['setTimeout']});const fixture=reviewRecycle();
  const original=fixture.authority.request;fixture.authority.request=async<T>(action:string,payload:any):Promise<T>=>action==='completeRecycle'?new Promise<T>(()=>{}):original<T>(action,payload);
  fixture.native.trash=async()=>new Promise<void>(()=>{});let stamps=0;fixture.native.fileStamp=async()=>{stamps++;return 'same';};
  let settled=false;const result=fixture.controller.deleteFile(fixture.selected);void result.then(()=>{settled=true;});
  for(let i=0;i<20&&stamps<2;i++)await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));
  context.mock.timers.tick(30001);for(let i=0;i<5;i++)await new Promise(resolve=>setImmediate(resolve));
  assert.ok(settled,'UI deadline waited for hung backend reporting');assert.equal((await result).outcome,'Unknown');
});
test('cross-row metadata uses explicit snapshot ID/revision without changing selected context or process effects', async () => {
  let consent=0;const {controller,requests}=setup(async()=>{consent++;return true;});const state=snapshot();
  const other={...state.scripts[0]!,id:'22222222-2222-2222-2222-222222222222',friendlyName:'Other row',description:'server manual',runAtLogin:null};state.scripts.push(other);controller.setSnapshot(state);
  const metadata={...metadataOf(other),runAsAdministrator:true};
  assert.equal((await controller.saveMetadata(other.id,metadata,state.revision)).outcome,'Confirmed');
  assert.deepEqual(requests.map(x=>x.action),['saveMetadata']);assert.equal(requests[0]!.payload.scriptId,other.id);assert.deepEqual(requests[0]!.payload.metadata,metadata);assert.equal(requests[0]!.payload.startupConsent,false);assert.equal(consent,0);
});
test('admin-only preference with existing login flag does not invent startup consent or enable global registration', async () => {
  let consent=0;const {controller,requests}=setup(async()=>{consent++;return true;});const state=snapshot();state.settings.launchManagerAtLogin=false;state.scripts[0]!.runAtLogin=true;controller.setSnapshot(state);
  await controller.saveMetadata(state.scripts[0]!.id,{...metadataOf(state.scripts[0]!),runAsAdministrator:true},state.revision);
  assert.equal(consent,0);assert.deepEqual(requests.map(x=>x.action),['saveMetadata']);assert.equal(requests[0]!.payload.startupConsent,false);assert.equal(controller.state.snapshot!.settings.launchManagerAtLogin,false);
});
test('native cancel never dispatches a prepared operation',async()=>{
  const {controller,requests}=setup(async()=>false);const result=await controller.operate('Stop',selection());
  assert.equal(result.outcome,'CancelledBeforeDispatch');assert.deepEqual(requests.map(r=>r.action),['prepare']);
});
test('selection changes while native confirmation is open cancel before perform',async()=>{
  let release!:(v:boolean)=>void;const modal=new Promise<boolean>(resolve=>release=resolve);
  const {controller,requests}=setup(()=>modal);const operation=controller.operate('Stop',selection());
  await new Promise(resolve=>setImmediate(resolve));
  controller.updateSelection({...selection(),query:{...selection().query,search:'outro',version:'9007199254740994'}});release(true);
  assert.equal((await operation).code,'StaleSelection');assert.equal(requests.filter(r=>r.action==='perform').length,0);
});
test('target hidden by a snapshot during confirmation is never dispatched',async()=>{
  let release!:(v:boolean)=>void;const {controller,requests}=setup(()=>new Promise(resolve=>release=resolve));
  const operation=controller.operate('Stop',selection());await new Promise(resolve=>setImmediate(resolve));
  controller.setSnapshot({...snapshot(),scripts:[]});release(true);
  assert.equal((await operation).code,'StaleSelection');assert.equal(requests.some(r=>r.action==='perform'),false);
});
test('revision-only observed updates retain selection and still defer plan authority to backend',async()=>{
  const {controller,requests}=setup(async()=>{controller.updateSelection({...selection(),stateRevision:'4'});return true;});
  assert.equal((await controller.operate('Stop',selection())).outcome,'Confirmed');
  const request=requests.find(r=>r.action==='perform');assert.ok(request);assert.equal(request.payload.planId,planId);assert.equal(request.payload.confirmed,true);assert.equal('plan' in request.payload,false);
});
test('renderer paths cannot configure an executable without a native picker authorization',async()=>{
  const {controller,requests,native}=setup();const settings={...snapshot().settings,editorPath:'C:\\evil.exe'};
  await assert.rejects(controller.saveSettings(settings),/seletor/);assert.equal(requests.length,0);
  native.files=async()=>['C:\\editor.exe'];assert.equal(await controller.pick('editor'),'C:\\editor.exe');
  await controller.saveSettings({...settings,editorPath:'C:\\editor.exe',window:{...settings.window,width:1}});
  assert.equal(requests[0]?.payload.settings.window.width,1200);
});
test('startup enabling always obtains independent native consent; cancel preserves settings',async()=>{
  const {controller,requests}=setup(async()=>false);
  assert.equal((await controller.saveSettings({...snapshot().settings,launchManagerAtLogin:true})).outcome,'CancelledBeforeDispatch');
  assert.equal((await controller.saveMetadata(scriptId,{friendlyName:'Teste',description:'',collectionId:null,runAtLogin:true,runAsAdministrator:false,launchBlocked:false,manualHotkeys:[]},snapshot().revision)).outcome,'CancelledBeforeDispatch');
  assert.equal(requests.length,0);
});
test('remove only affects catalog after native confirmation and matching revision',async()=>{
  const {controller,requests}=setup();await controller.remove(scriptId,snapshot().revision);assert.deepEqual(requests,[{action:'remove',payload:{scriptId}}]);
  assert.equal((await controller.remove(scriptId,'1')).code,'StaleSelection');assert.equal(requests.length,1);
});
test('mutations are serialized and an unavailable host never replays effects',async()=>{
  const {controller,requests,authority}=setup();let release!:()=>void;
  authority.request=async<T>(action:string,payload:any)=>{requests.push({action,payload});if(action==='saveCollection'&&requests.length===1)await new Promise<void>(resolve=>release=resolve);return confirmed as T;};
  const first=controller.saveCollection(null,'A');const second=controller.saveCollection(null,'B');await new Promise(resolve=>setImmediate(resolve));assert.equal(requests.length,1);
  controller.setConnection('failed','EOF');release();await first;await assert.rejects(second,/indisponível/);assert.equal(requests.length,1);
});
test('filter visibility excludes hidden collections and truthful unknown status',()=>{
  const state=snapshot();assert.equal(selectionVisible(state,selection()),true);
  assert.equal(selectionVisible(state,{...selection(),query:{...selection().query,status:'Running'}}),false);
  assert.equal(selectionVisible(state,{...selection(),query:{...selection().query,collectionId:planId}}),false);
});

test('selection search uses header description with the same manual fallback as the visible row',()=>{
  const state=snapshot();const row=state.scripts[0]!;
  row.description='manual-only-term';row.headerDescription='Header-only-term';
  const matching=(search:string)=>selectionVisible(state,{...selection(),query:{...selection().query,search}});
  assert.equal(matching('header-only-term'),true,'Header-only visible row was rejected by selection authority');
  assert.equal(matching('manual-only-term'),false,'Hidden manual fallback matched despite a present header');
  row.headerDescription='   ';
  assert.equal(matching('manual-only-term'),true,'Blank header did not use manual fallback');
  row.headerDescription=null;
  assert.equal(matching('manual-only-term'),true,'Absent header did not use manual fallback');
});

function batchSetup(count=2){
  const env={state:runningSnapshot(count),clock:0,refreshes:0,confirmations:0,cancelled:false,maxTokens:0,requests:[] as {action:string;payload:any}[],tokens:new Map<string,OperationPlan>(),onRefresh:(_count:number)=>{},onPrepare:(_plan:OperationPlan)=>{},onPerform:async(_plan:OperationPlan):Promise<OperationResult>=>({...confirmed}),onOther:async(_action:string):Promise<OperationResult>=>({...confirmed}),confirm:async()=>true};
  let next=0;
  const authority={request:async<T>(action:string,payload:any):Promise<T>=>{
    env.requests.push({action,payload});
    if(action==='refresh'){env.onRefresh(++env.refreshes);env.state={...env.state,revision:String(BigInt(env.state.revision)+1n)};return structuredClone(env.state) as T;}
    if(action==='prepare'){
      const row=env.state.scripts.find(s=>s.id===payload.selection.scriptId)!;
      const plan:OperationPlan={correlationId:planId,scriptId:row.id,operation:'Stop',stateRevision:env.state.revision,queryVersion:payload.selection.query.version,expectedIdentity:structuredClone(row.process?.identity??null),allowed:true,requiresConfirmation:true,reason:'None',selection:{scriptId:payload.selection.scriptId,query:structuredClone(payload.selection.query),stateRevision:payload.selection.stateRevision}};
      env.onPrepare(plan);const token=String(++next);env.tokens.set(token,plan);env.maxTokens=Math.max(env.maxTokens,env.tokens.size);return {planId:token,plan} as T;
    }
    if(action==='perform'){
      const plan=env.tokens.get(payload.planId);env.tokens.delete(payload.planId);if(!plan)return {...confirmed,outcome:'Failed',code:'StaleSelection'} as T;
      if(!payload.confirmed)return {...confirmed,outcome:'CancelledBeforeDispatch',code:'Cancelled'} as T;
      const result=await env.onPerform(plan);
      if(result.outcome==='Confirmed'&&result.code==='None')env.state={...env.state,scripts:env.state.scripts.map(row=>row.id===plan.scriptId?{...row,status:'Stopped',process:null}:row)};
      return result as T;
    }
    return await env.onOther(action) as T;
  }};
  const native:NativeBoundary={files:async()=>[],confirm:async()=>{env.confirmations++;return env.confirm();},alive:()=>true,placement:()=>snapshot().settings.window};
  const controller=new ManagerController(authority,native,()=>{});controller.setSnapshot(structuredClone(env.state));controller.setConnection('ready');controller.updateSelection({...selection(),query:{...selection().query,search:'hidden by renderer'}});
  const run=(finalObservation?:Parameters<ManagerController['stopScriptsForExit']>[0]['finalObservation'])=>controller.stopScriptsForExit({cancelled:()=>env.cancelled,now:()=>env.clock,finalObservation});
  return {env,controller,run};
}
const effectRequests=(env:ReturnType<typeof batchSetup>['env'])=>env.requests.filter(row=>row.action==='perform'&&row.payload.confirmed);
const settle=()=>new Promise<void>(resolve=>setImmediate(resolve));
const stopped=(state:ManagerSnapshot):ManagerSnapshot=>({...state,scripts:state.scripts.map(row=>({...row,status:'Stopped',process:null}))});

test('stop-and-exit Cancel sends no prepare/perform and releases the mutating lane',async()=>{
  const {env,controller,run}=batchSetup();env.confirm=async()=>false;const result=await run();assert.equal(result.cancelled,true);assert.equal(result.canExit,false);assert.deepEqual(env.requests.map(row=>row.action),['refresh']);assert.equal(controller.state.busy,false);await controller.saveCollection(null,'after cancel');
});
test('stop-and-exit uses all catalog identities despite renderer filters, with separate phase budgets',async()=>{
  const {env,run}=batchSetup();env.onRefresh=()=>{env.clock+=3000;};env.confirm=async()=>{env.clock+=120000;return true;};env.onPrepare=()=>{env.clock+=4000;};env.onPerform=async()=>{env.clock+=10000;return {...confirmed};};
  let finalBudget=0;const result=await run(async(request,ms)=>{finalBudget=ms;env.clock+=30000;return request();});
  assert.equal(result.canExit,true);assert.equal(result.confirmedStops,2);assert.equal(env.confirmations,1);assert.equal(finalBudget,30000);assert.equal(effectRequests(env).length,2);assert.equal(env.maxTokens,1);
  for(const call of env.requests.filter(row=>row.action==='prepare'))assert.deepEqual({...call.payload.selection.query,version:'batch'}, {search:'',status:'All',sort:'Name',collectionId:null,version:'batch'});
});
test('already stopped complete scope exits without pretending to send Stop or requesting consent',async()=>{
  const {env,run}=batchSetup();env.state=stopped(env.state);const result=await run();assert.equal(result.canExit,true);assert.equal(result.confirmedStops,0);assert.equal(env.confirmations,0);assert.equal(env.refreshes,2);assert.equal(effectRequests(env).length,0);
});
test('dispatch expiry during positive prepare time discards token and later stopped observation cannot authorize exit',async()=>{
  const {env,run}=batchSetup();env.onPrepare=()=>{env.clock+=31000;};const result=await run(async(request,ms)=>{assert.equal(ms,30000);env.state=stopped(env.state);return request();});
  assert.equal(result.expired,true);assert.equal(result.canExit,false);assert.equal(effectRequests(env).length,0);assert.equal(env.requests.filter(r=>r.action==='prepare').length,1);assert.equal(env.tokens.size,0);
});
test('next target needs its complete30second budget; no further token or effect is scheduled after expiry',async()=>{
  const {env,run}=batchSetup(3);let prepares=0;env.onPrepare=()=>{env.clock+=++prepares===1?5000:20000;};env.onPerform=async()=>{env.clock+=10000;return {...confirmed};};
  const result=await run(async request=>{env.state=stopped(env.state);return request();});assert.equal(result.expired,true);assert.equal(result.confirmedStops,1);assert.equal(result.canExit,false);assert.equal(prepares,2);assert.equal(effectRequests(env).length,1);assert.equal(env.tokens.size,0);
});
test('cancellation during in-flight Stop waits for its outcome and never schedules or replays another effect',async()=>{
  const {env,run}=batchSetup();let release!:(value:OperationResult)=>void;env.onPerform=()=>new Promise(resolve=>release=resolve);const task=run(async request=>{env.state=stopped(env.state);return request();});await settle();assert.equal(effectRequests(env).length,1);env.cancelled=true;release({...confirmed});const result=await task;assert.equal(result.cancelled,true);assert.equal(result.canExit,false);assert.equal(effectRequests(env).length,1);
});
test('batch owns mutating lane before drain and duplicate batch cannot queue another stop',async()=>{
  const {env,controller,run}=batchSetup();let release!:(value:boolean)=>void;env.confirm=()=>new Promise(resolve=>release=resolve);const first=run();await settle();assert.equal((await run()).problems[0],'BatchBusy');await assert.rejects(controller.saveCollection(null,'blocked'),/parada/);await assert.rejects(controller.runLogin(),/parada/);assert.equal(env.requests.some(row=>row.action==='runLogin'),false);release(false);await first;assert.equal(effectRequests(env).length,0);
});
for(const status of ['Unknown','Multiple','Blocked','Missing'] as const)test(`mixed ${status} row is untouched and prevents exit while confirmed safe row can stop`,async()=>{
  const {env,run}=batchSetup();env.state.scripts[1]={...env.state.scripts[1]!,status,process:null};const result=await run();assert.equal(result.canExit,false);assert.equal(result.confirmedStops,1);assert.equal(effectRequests(env).length,1);assert.equal(result.items[1]?.outcome,'untouched');
});
for(const condition of ['elevated','window','pending','wrong-path'] as const)test(`unverified ${condition} target never receives prepare/Stop`,async()=>{
  const {env,run}=batchSetup(1);const row=env.state.scripts[0]!;
  if(condition==='elevated'){row.runAsAdministrator=true;row.process!.controlUnavailableReason='ManualElevationRequired';}if(condition==='window')row.process!.windowVerified=false;if(condition==='pending')row.issues=[{code:'PendingOperation',detail:null,scriptId:row.id}];if(condition==='wrong-path')row.process!.identity.scriptPath='C:\\foreign.ahk';
  const result=await run();assert.equal(result.canExit,false);assert.equal(env.requests.some(r=>r.action==='prepare'),false);assert.equal(effectRequests(env).length,0);
});
const identityChanges:Record<keyof ProcessIdentity,string|number>={pid:999,creationFileTime:'9007199254740999',executablePath:'C:\\foreign.exe',scriptPath:'C:\\foreign.ahk',userSid:'S-1-5-21-other',sessionId:88,scriptHash:'c'.repeat(64),interpreterHash:'d'.repeat(64)};
for(const key of Object.keys(identityChanges) as (keyof ProcessIdentity)[])test(`consent cannot follow changed ${key} identity`,async()=>{
  const {env,run}=batchSetup(1);env.onRefresh=count=>{if(count===2)env.state.scripts[0]!.process!.identity={...env.state.scripts[0]!.process!.identity,[key]:identityChanges[key]};};const result=await run();assert.equal(result.canExit,false);assert.equal(effectRequests(env).length,0);assert.equal(env.requests.some(r=>r.action==='prepare'),false);
});
for(const key of Object.keys(identityChanges) as (keyof ProcessIdentity)[])test(`prepared changed ${key} is discarded without effect`,async()=>{
  const {env,run}=batchSetup(1);env.onPrepare=plan=>{plan.expectedIdentity={...plan.expectedIdentity!,[key]:identityChanges[key]};};const result=await run();assert.equal(result.canExit,false);assert.equal(effectRequests(env).length,0);assert.equal(env.tokens.size,0);
});
test('more than backend token capacity denied plans are consumed one at a time',async()=>{
  const {env,run}=batchSetup(40);env.onPrepare=plan=>{plan.allowed=false;plan.reason='WindowUnverified';};const result=await run();assert.equal(result.canExit,false);assert.equal(env.maxTokens,1);assert.equal(env.tokens.size,0);assert.equal(env.requests.filter(r=>r.action==='prepare').length,40);assert.equal(effectRequests(env).length,0);
});
test('stale prepared revision and material catalog change cannot expand consent',async()=>{
  const a=batchSetup(1);a.env.onPrepare=plan=>{plan.stateRevision='999';};assert.equal((await a.run()).canExit,false);assert.equal(effectRequests(a.env).length,0);assert.equal(a.env.tokens.size,0);
  for(const change of ['delete','rename','add']){const b=batchSetup(1);b.env.onRefresh=count=>{if(count!==2)return;if(change==='delete')b.env.state.scripts=[];else if(change==='rename')b.env.state.scripts[0]!.friendlyName='different';else b.env.state.scripts.push(runningSnapshot(2).scripts[1]!);};assert.equal((await b.run()).canExit,false);assert.equal(effectRequests(b.env).length,0);}
});
for(const mode of ['owner','readonly','pending'] as const)test(`changed ${mode} authority prevents dispatch`,async()=>{
  const {env,run}=batchSetup(1);env.onRefresh=count=>{if(count!==2)return;if(mode==='owner')env.state.effectsAvailable=false;if(mode==='readonly')env.state.readOnly=true;if(mode==='pending')env.state.issues=[{code:'PendingOperation',detail:null,scriptId:null}];};assert.equal((await run()).canExit,false);assert.equal(effectRequests(env).length,0);
});
for(const outcome of ['Accepted','Pending','Unknown','Partial','Failed'] as const)test(`${outcome} Stop result cannot turn into successful exit through late observation`,async()=>{
  const {env,run}=batchSetup(1);env.onPerform=async()=>({...confirmed,outcome,code:outcome==='Failed'?'ExitNotObserved':'None'});const result=await run(async request=>{env.state=stopped(env.state);return request();});assert.equal(result.canExit,false);assert.equal(result.confirmedStops,0);assert.equal(effectRequests(env).length,1);
});
test('original process freshly observed exited can count stopped; replacement and final new row cannot',async()=>{
  const first=batchSetup(1);first.env.onRefresh=count=>{if(count===2)first.env.state=stopped(first.env.state);};assert.equal((await first.run()).canExit,true);assert.equal(effectRequests(first.env).length,0);
  const replacement=batchSetup(1);replacement.env.onRefresh=count=>{if(count===2)replacement.env.state.scripts[0]!.process!.identity.pid++;};assert.equal((await replacement.run()).canExit,false);assert.equal(effectRequests(replacement.env).length,0);
  const added=batchSetup(1);added.env.onRefresh=count=>{if(count===3)added.env.state.scripts.push(runningSnapshot(2).scripts[1]!);};assert.equal((await added.run()).canExit,false);assert.equal(effectRequests(added.env).length,1);
});
test('final observation timeout/failure keeps lane usable and cannot authorize shutdown',async()=>{
  const {env,controller,run}=batchSetup(1);const result=await run(async(_request,ms)=>{assert.equal(ms,30000);throw new Error('DeadlineExceeded');});assert.equal(result.canExit,false);assert.equal(result.confirmedStops,1);assert.equal(controller.state.busy,false);await controller.saveCollection(null,'after failure');assert.equal(effectRequests(env).length,1);
});
test('cancel after prepare consumes the token without an effect',async()=>{
  const {env,run}=batchSetup(1);env.onPrepare=()=>{env.cancelled=true;};const result=await run();assert.equal(result.cancelled,true);assert.equal(result.canExit,false);assert.equal(env.tokens.size,0);assert.equal(effectRequests(env).length,0);
});
test('authority loss after prepare attempts effect-free discard and prevents exit',async()=>{
  const {env,controller,run}=batchSetup(1);env.onPrepare=()=>{controller.setConnection('failed','fake EOF');};assert.equal((await run()).canExit,false);assert.equal(effectRequests(env).length,0);assert.equal(env.tokens.size,0);
});
test('a perform completing beyond dispatch budget cannot authorize exit',async()=>{
  const {env,run}=batchSetup(1);env.onPerform=async()=>{env.clock+=61000;return {...confirmed};};const result=await run();assert.equal(result.expired,true);assert.equal(result.canExit,false);assert.equal(effectRequests(env).length,1);
});
test('drain finishes prior mutation before observation and spends none of dispatch window',async()=>{
  const {env,controller,run}=batchSetup(1);let release!:(value:OperationResult)=>void;env.onOther=()=>new Promise(resolve=>release=resolve);const prior=controller.runLogin();await settle();const task=run();await settle();assert.equal(env.requests[0]?.action,'runLogin');assert.equal(env.refreshes,0);await assert.rejects(controller.saveCollection(null,'competitor'),/parada/);env.clock+=90000;release({...confirmed});await prior;const result=await task;assert.equal(result.canExit,true);assert.equal(effectRequests(env).length,1);
});
test('uncertain dispatched Stop is not retried even if final observation reports stopped',async()=>{
  const {env,run}=batchSetup(1);env.onPerform=async()=>{throw new Error('fake request timeout after send');};const result=await run(async request=>{env.state=stopped(env.state);return request();});assert.equal(result.canExit,false);assert.equal(result.items[0]?.outcome,'unknown');assert.equal(effectRequests(env).length,1);
});
test('untrusted initial observation and new final pending record prevent exit',async()=>{
  const initial=batchSetup(1);initial.env.state.observedAt=null;assert.equal((await initial.run()).canExit,false);assert.equal(initial.env.confirmations,0);assert.equal(effectRequests(initial.env).length,0);
  const final=batchSetup(1);final.env.onRefresh=count=>{if(count===3)final.env.state.issues=[{code:'PendingOperation',detail:null,scriptId:null}];};assert.equal((await final.run()).canExit,false);assert.equal(effectRequests(final.env).length,1);
});
