import test from 'node:test';
import assert from 'node:assert/strict';
import { ManagerController, selectionVisible, type NativeBoundary } from '../../src/main/controller.js';
import { snapshot, selection, scriptId, planId } from './fixtures.js';
import type { OperationResult } from '../../src/shared/contracts.js';
const confirmed:OperationResult={outcome:'Confirmed',code:'None',detail:null,correlationId:null};
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
