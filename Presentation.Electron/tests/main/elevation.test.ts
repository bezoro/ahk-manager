import test from 'node:test';
import assert from 'node:assert/strict';
import { AdministrationPreflight, ElevationCoordinator, type ElevationBoundary, type PreflightRequest } from '../../src/main/elevation.js';
import type { ElevationStatus } from '../../src/shared/contracts.js';

const clean={dirtyDrafts:false,dialogOpen:false,pendingWork:false};
test('preflight binds operation, phase, nonce and current document, and rejects provider unmount',async()=>{
  const gate=new AdministrationPreflight();gate.register(true);let current=true;let sent:PreflightRequest|null=null;
  const pending=gate.check('operation','begin',request=>{sent=request;},()=>current,80);
  assert.ok(sent);const request=sent as unknown as PreflightRequest;
  gate.reply({...request,nonce:'forged'},clean);gate.reply({...request,phase:'commit'},clean);
  current=false;gate.reply(request,clean);assert.equal(await pending,false);
  current=true;gate.register(true);const unmounted=gate.check('operation','commit',()=>{},()=>current,80);gate.register(false);assert.equal(await unmounted,false);
});
test('absent provider and missing/throwing provider replies cannot authorize Begin',async()=>{
  const gate=new AdministrationPreflight();assert.equal(await gate.check('operation','begin',()=>assert.fail('no provider'),()=>true,1),false);
  gate.register(true);assert.equal(await gate.check('operation','begin',()=>{throw new Error('provider unavailable');},()=>true,20),false);
  assert.equal(await gate.check('operation','begin',()=>{},()=>true,1),false);
});
test('fresh Begin and Commit preflights observe dirty, dialog and pending changes',async()=>{
  for(const blocked of [{...clean,dirtyDrafts:true},{...clean,dialogOpen:true},{...clean,pendingWork:true},null]){
    const gate=new AdministrationPreflight();gate.register(true);
    assert.equal(await gate.check('operation','commit',request=>gate.reply(request,blocked),()=>true),false);
  }
  const gate=new AdministrationPreflight();gate.register(true);
  assert.equal(await gate.check('operation','begin',request=>gate.reply(request,clean),()=>true),true);
});
function fixture(options:{cli?:boolean;preflight?:boolean[];confirm?:boolean;statuses?:(action:string,index:number)=>Partial<ElevationStatus>|Error;drain?:()=>Promise<void>;persist?:()=>Promise<void>}={}){
  const calls:{action:string;payload:any}[]=[];const phases:string[]=[];const quiescence:boolean[]=[];let quit=0;let confirmations=0;let tick=0;
  const boundary:ElevationBoundary={eligible:()=>true,confirm:async()=>{confirmations++;return options.confirm??true;},preflight:async(_id,phase)=>{phases.push(phase);return options.preflight?.shift()??true;},quiesce:value=>quiescence.push(value),drain:options.drain??(async()=>{}),persistPlacement:options.persist??(async()=>{}),now:()=>tick,
    request:async(action,payload)=>{const p=payload as any;calls.push({action,payload:p});const answer=options.statuses?.(action,calls.length)??{state:action==='commitAdministration'?'CommitAcked':'Ready'};tick+=1000;if(answer instanceof Error)throw answer;return {operationId:p.operationId,revision:String(calls.length),state:'Ready',reason:null,receiptId:action==='commitAdministration'?'22222222-2222-2222-2222-222222222222':null,canResumeNormal:false,...answer};},quitAfterAcknowledgement:async()=>{quit++;}};
  return {coordinator:new ElevationCoordinator(boundary),calls,phases,quiescence,get quit(){return quit;},get confirmations(){return confirmations;}};
}
test('native cancel and dirty guard dispatch no ticket; CLI skips only native application prompt',async()=>{
  const cancelled=fixture({confirm:false});assert.equal((await cancelled.coordinator.request()).outcome,'CancelledBeforeDispatch');assert.equal(cancelled.calls.length,0);
  const dirty=fixture({preflight:[false]});await dirty.coordinator.request(true);assert.equal(dirty.calls.length,0);assert.equal(dirty.confirmations,0);
  const cli=fixture();await cli.coordinator.request(true);assert.equal(cli.confirmations,0);assert.deepEqual(cli.phases,['begin','begin','commit']);assert.equal(cli.quit,1);
});
test('automatic administrative launch preserves login intent only in its authenticated begin request',async()=>{
  const login=fixture();
  assert.equal((await login.coordinator.request(true,undefined,true)).outcome,'Confirmed');
  assert.equal(login.calls[0]?.payload.resumeLogin,true);
  assert.equal(login.calls.filter(call=>call.action==='beginAdministration').length,1);
  const manual=fixture();await manual.coordinator.request(true);
  assert.equal(manual.calls[0]?.payload.resumeLogin,undefined);
  const scoped=fixture();await scoped.coordinator.request(true,'33333333-3333-3333-3333-333333333333',true);
  assert.equal(scoped.calls[0]?.action,'beginStartupContinuation');
  assert.equal(scoped.calls[0]?.payload.resumeLogin,undefined,'A scoped continuation must never rerun the general login loop');
});
test('drain and placement precede Begin; active lane blocks concurrent requests and quits only after receipt',async()=>{
  let release!:()=>void;const f=fixture({drain:()=>new Promise(resolve=>{release=resolve;})});const request=f.coordinator.request();
  for(let i=0;i<5;i++)await Promise.resolve();assert.equal(f.coordinator.active,true);assert.equal(f.calls.length,0);assert.equal((await f.coordinator.request()).code,'PendingOperation');assert.equal(f.quit,0);
  release();assert.equal((await request).outcome,'Confirmed');assert.equal(f.quit,1);assert.equal(f.calls.filter(call=>call.action==='beginAdministration').length,1);
  const failed=fixture({persist:async()=>{throw new Error('disk');}});assert.equal((await failed.coordinator.request()).code,'StorageFailure');assert.deepEqual(failed.quiescence,[true,false]);assert.equal(failed.calls.length,0);
});
test('lost Begin and Commit responses resolve through status without mutation replay',async()=>{
  const f=fixture({statuses:action=>action==='beginAdministration'||action==='commitAdministration'?new Error('RPC timeout'): {state:f.calls.some(call=>call.action==='commitAdministration')?'CommitAcked':'Ready',receiptId:f.calls.some(call=>call.action==='commitAdministration')?'22222222-2222-2222-2222-222222222222':null}});
  assert.equal((await f.coordinator.request()).outcome,'Confirmed');assert.deepEqual(f.calls.map(call=>call.action),['beginAdministration','administrationStatus','commitAdministration','administrationStatus']);assert.equal(f.quit,1);assert.deepEqual(f.quiescence,[true]);
});
test('unresolved status retains quiescence; later explicit check resumes only authenticated safe state',async()=>{
  let resume=false;const f=fixture({statuses:()=>resume?{state:'Denied',canResumeNormal:true,reason:'Cancelled'}:new Error('timeout')});
  assert.equal((await f.coordinator.request()).outcome,'Unknown');assert.equal(f.coordinator.active,true);assert.deepEqual(f.quiescence,[true]);assert.equal(f.quit,0);
  resume=true;assert.equal((await f.coordinator.request()).outcome,'CancelledBeforeDispatch');assert.equal(f.coordinator.active,false);assert.deepEqual(f.quiescence,[true,false]);assert.equal(f.calls.filter(call=>call.action==='beginAdministration').length,1);
});
test('dirty change before Commit cancels ticket; cancellation unknown never resumes or commits',async()=>{
  const f=fixture({preflight:[true,true,false],statuses:action=>action==='cancelAdministration'?{state:'Unknown'}:{state:'Ready'}});
  assert.equal((await f.coordinator.request()).outcome,'Unknown');assert.deepEqual(f.calls.map(call=>call.action),['beginAdministration','cancelAdministration']);assert.equal(f.quit,0);assert.equal(f.coordinator.active,true);assert.deepEqual(f.quiescence,[true]);
});
test('wrong operation, regressing revision, receiptless acknowledgement and expiry never quit/resume',async()=>{
  for(const status of [{operationId:'forged',state:'Ready'},{state:'Expired'},{state:'Unknown'},{state:'CommitAcked',receiptId:null}]){
    const f=fixture({statuses:()=>status as Partial<ElevationStatus>});assert.equal((await f.coordinator.request()).outcome,'Unknown');assert.equal(f.quit,0);assert.deepEqual(f.quiescence,[true]);
  }
  const stale=fixture({statuses:(_action,index)=>index===1?{state:'PendingApproval',revision:'2'}:{state:'Ready',revision:'1'}});assert.equal((await stale.coordinator.request()).code,'IdentityChanged');assert.equal(stale.quit,0);
});
test('continuation uses a separate elevation attempt and never falls back to generic login or replay after lost response',async()=>{
  const continuationId='33333333-3333-3333-3333-333333333333';
  const f=fixture({statuses:action=>action==='beginStartupContinuation'||action==='commitAdministration'?new Error('lost receipt'):{state:f.calls.some(call=>call.action==='commitAdministration')?'CommitAcked':'Ready',receiptId:f.calls.some(call=>call.action==='commitAdministration')?'22222222-2222-2222-2222-222222222222':null}});
  assert.equal((await f.coordinator.request(true,continuationId)).outcome,'Confirmed');
  assert.deepEqual(f.calls.map(call=>call.action),['beginStartupContinuation','administrationStatus','commitAdministration','administrationStatus']);
  assert.equal(f.calls[0]!.payload.continuationId,continuationId);assert.notEqual(f.calls[0]!.payload.operationId,continuationId);assert.equal(f.quit,1);assert.equal(f.confirmations,0);
});
