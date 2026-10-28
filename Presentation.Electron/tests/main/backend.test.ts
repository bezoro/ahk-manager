import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import fs from 'node:fs';
import { snapshot as validateSnapshot } from '../../src/shared/validation.js';
import { BackendClient, MAX_FRAME, UnknownAfterDispatchError } from '../../src/main/backend.js';
import { snapshot, withHeaderDescription, planId } from './fixtures.js';
const nonce='a'.repeat(64),host='C:\\App\\Bezoro.AhkManager.exe';

test('strict additive header field accepts absent/null/4000 and rejects forged type/NUL/length/unknown properties', () => {
  const base = snapshot();
  for (const value of [undefined, null, 'line1\n<script>literal</script>', 'x'.repeat(4000)]) {
    const state = value === undefined ? structuredClone(base) : withHeaderDescription(value);
    assert.equal(validateSnapshot(state), true);
  }
  for (const value of [42, {}, 'a\0b', 'x'.repeat(4001)]) {
    const state = structuredClone(base); (state.scripts[0] as any).headerDescription = value;
    assert.equal(validateSnapshot(state), false);
  }
  const forged = structuredClone(base); (forged.scripts[0] as any).unknownHeader = 'forged'; assert.equal(validateSnapshot(forged), false);
});

test('actual source-generated NUL-file snapshot stays valid through broker subscription and connection', { skip: !process.env.AHK_HEADER_SNAPSHOT_INPUT }, async () => {
  const path = process.env.AHK_HEADER_SNAPSHOT_INPUT; assert.ok(path, 'Bind the backend fake snapshot artifact for this integrated check');
  const state = JSON.parse(fs.readFileSync(path, 'utf8')) as ReturnType<typeof snapshot>;
  assert.equal(validateSnapshot(state), true); assert.equal(state.scripts[0]!.headerDescription, null); assert.equal(state.scripts[0]!.description, 'manual fallback survives');
  assert.ok(state.scripts[0]!.issues.some(issue => issue.code === 'PartialAnalysis' && !issue.detail?.includes('\0')));
  const { client, child, envelope } = setup(); await client.start(); let observed: unknown = null;
  client.on('snapshot', value => { observed = value; });
  child.stdout.write(JSON.stringify(envelope('snapshot', null, state, 'snapshot')) + '\n');
  assert.deepEqual(observed, state); assert.equal(client.ready, true); await client.shutdown();
});
function setup(options:{proof?:Record<string,unknown>;ignore?:string;invalid?:string;exitOnEnd?:boolean;noShutdownExit?:boolean;failedSpawn?:boolean;adoption?:string;reply?:(action:string,payload:unknown)=>unknown}={}){
  const calls:any[]=[];const child=new EventEmitter() as any;child.pid=options.failedSpawn?undefined:9001;child.stdout=new PassThrough();child.stderr=new PassThrough();
  const envelope=(action:string,id:string|null,payload:unknown,kind='response')=>({version:1,kind,id,session:nonce,action,code:'Ok',payload,error:null});
  child.stdin=new Writable({write(chunk,_encoding,callback){
    const request=JSON.parse(chunk.toString());calls.push(request);callback();
    if(options.ignore===request.action)return;
    const payload=options.reply?.(request.action,request.payload)??(request.action==='hello'?{protocolVersion:1,profileRoot:'C:\\FixtureProfile',hostExecutable:host}:request.action==='shutdown'?{outcome:'Confirmed',code:'None',detail:null,correlationId:null,succeeded:true}:snapshot());
    setImmediate(()=>{child.stdout.write(options.invalid??JSON.stringify(envelope(request.action,request.id,payload))+'\n');if(request.action==='shutdown'&&!options.noShutdownExit){child.emit('exit',0,null);child.stdout.end();}});
  },final(callback){callback();if(options.exitOnEnd)setImmediate(()=>child.emit('exit',0,null));}});
  const spawn=((executable:any,args:any,config:any)=>{assert.equal(executable,'C:\\App\\backend\\AhkManager.Backend.exe');assert.equal(config.shell,false);assert.equal(config.windowsHide,true);assert.equal(config.detached,false);assert.deepEqual(args,['--parent-pid',String(process.pid),'--session',nonce,...(options.adoption?['--startup-adoption',options.adoption]:[])]);setImmediate(()=>{if(options.failedSpawn){child.emit('error',new Error('spawn ENOENT'));return;}child.emit('spawn');child.stdout.write(JSON.stringify(envelope('hello',null,{parentPid:process.pid,parentCreated:'134000000000000001',parentExecutable:host,childPid:9001,childCreated:'134000000000000002',userSid:'S-1-5-21-test',sessionId:1,authorityMode:'Normal',...options.proof},'parentProof'))+'\n');});return child;}) as any;
  const client=new BackendClient({executable:'C:\\App\\backend\\AhkManager.Backend.exe',hostExecutable:host,session:nonce,createChild:spawn,timeoutMs:1000,...(options.adoption?{startupAdoption:options.adoption}:{})});
  return {client,child,calls,envelope};
}
test('trusted child proof and hello retain exact FILETIME and revision strings',async()=>{
  const {client,calls}=setup();await client.start();assert.equal(calls[0].payload.parentCreated,'134000000000000001');
  const state=await client.request<any>('snapshot',{});assert.equal(state.revision,'9223372036854775807');await client.shutdown();assert.equal(client.exited,true);
});
test('adoption descriptor is passed only as blocked startup data and late adoption results are validated without replay',async()=>{
  const descriptor=`11111111-1111-1111-1111-111111111111|${'a'.repeat(64)}|10|100|${'b'.repeat(64)}`;
  const f=setup({adoption:descriptor,proof:{authorityMode:'Administrator'},ignore:'adoptStartupContinuation'});await f.client.start();
  await assert.rejects(f.client.request('adoptStartupContinuation',{},1),UnknownAfterDispatchError);assert.equal(f.client.ready,true);
  const call=f.calls.find(call=>call.action==='adoptStartupContinuation');assert.ok(call);
  f.child.stdout.write(JSON.stringify(f.envelope('adoptStartupContinuation',call.id,{outcome:'Partial',code:'None',detail:'Dois scripts normais verificados.',correlationId:null,succeeded:false}))+'\n');
  assert.equal(f.calls.filter(call=>call.action==='adoptStartupContinuation').length,1);await f.client.shutdown();
});
test('forged parent/child proof refuses hello and effects',async()=>{
  for(const proof of [{parentPid:1},{childPid:1},{parentExecutable:'C:\\Evil.exe'}]){const {client,calls}=setup({proof});await assert.rejects(client.start(),/proof mismatch/);assert.equal(calls.length,0);}
});
test('timeout after dispatch is unknown, closes intake and never replays',async()=>{
  const {client,calls}=setup({ignore:'refresh',exitOnEnd:true});await client.start();
  await assert.rejects(client.request('refresh',{},20),UnknownAfterDispatchError);
  await assert.rejects(client.request('refresh',{}));assert.equal(calls.filter(r=>r.action==='refresh').length,1);
});

test('ticket timeout preserves authenticated session and validates late responses without replay',async()=>{
  const id='33333333-3333-3333-3333-333333333333';
  for(const action of ['beginAdministration','administrationStatus','commitAdministration','cancelAdministration']){
    const f=setup({ignore:action});await f.client.start();
    await assert.rejects(f.client.request(action,{operationId:id},1),UnknownAfterDispatchError);assert.equal(f.client.ready,true);
    const call=f.calls.find(call=>call.action===action);assert.ok(call);
    f.child.stdout.write(JSON.stringify(f.envelope(action,call.id,{operationId:id,revision:'1',state:'Unknown',reason:null,receiptId:null,canResumeNormal:false}))+'\n');
    assert.equal(f.client.ready,true);await f.client.request('refresh',{});assert.equal(f.calls.filter(call=>call.action===action).length,1);await f.client.shutdown();
  }
});
test('late expired-ticket wrong action, nonce or malformed status remains a session failure',async()=>{
  for(const wrong of ['action','nonce','payload']){
    const f=setup({ignore:'beginAdministration'});await f.client.start();await assert.rejects(f.client.request('beginAdministration',{operationId:planId},1));
    const call=f.calls.find(call=>call.action==='beginAdministration');const response=f.envelope(wrong==='action'?'cancelAdministration':'beginAdministration',call.id,wrong==='payload'?{state:'forged'}:{operationId:planId,revision:'1',state:'Unknown',reason:null,receiptId:null,canResumeNormal:false});if(wrong==='nonce')response.session='b'.repeat(64);
    f.child.stdout.write(JSON.stringify(response)+'\n');assert.equal(f.client.ready,false);await assert.rejects(f.client.request('refresh',{}));f.child.emit('exit',1,null);
  }
});
test('malformed UTF8, duplicate properties, unsolicited response and oversized frames fail closed',async()=>{
  for(const kind of ['utf8','duplicate','unsolicited','oversize']){
    const {client,child,envelope}=setup();await client.start();let failure:Error|null=null;client.on('unavailable',(error:Error)=>failure=error);
    if(kind==='utf8')child.stdout.write(Buffer.from([0xff,10]));
    if(kind==='duplicate')child.stdout.write('{"version":1,"version":1}\n');
    if(kind==='unsolicited')child.stdout.write(JSON.stringify(envelope('snapshot','99',snapshot()))+'\n');
    if(kind==='oversize')child.stdout.write(Buffer.alloc(MAX_FRAME+1,32));
    assert.ok(failure,kind);assert.equal(client.ready,false);
  }
});
test('pending request bound rejects extra intake without silently dispatching',async()=>{
  const {client,calls,child}=setup({ignore:'snapshot'});await client.start();
  const pending=Array.from({length:32},()=>client.request('snapshot',{}).catch(()=>undefined));
  await assert.rejects(client.request('snapshot',{}),/queue full/);assert.equal(calls.filter(r=>r.action==='snapshot').length,32);
  child.emit('exit',1,null);await Promise.all(pending);
});
test('shutdown acknowledgement without observed child exit remains unknown and never kills a process',async()=>{
  const {client,child,calls}=setup({noShutdownExit:true});let killed=false;child.kill=()=>{killed=true;};await client.start();
  await assert.rejects(client.shutdown(30),/Encerramento desconhecido/);assert.equal(client.exited,false);assert.equal(killed,false);assert.equal(calls.filter(r=>r.action==='shutdown').length,1);
  child.emit('exit',0,null);
});
test('error-only OS spawn failure with no PID proves no living child and allows retry/Exit',async()=>{
  const {client,calls}=setup({failedSpawn:true});await assert.rejects(client.start(),/ENOENT/);assert.equal(client.pid,null);assert.equal(client.exited,true);assert.equal(calls.length,0);
  // Main's retry guard uses this same positive no-child result; a new session is deliberate.
  const retry=setup();await retry.client.start();await retry.client.shutdown();
});
test('an error after confirmed spawn is not exit evidence and cannot permit a replacement child',async()=>{
  const {client,child}=setup();await client.start();child.emit('error',new Error('live child IPC error'));
  assert.equal(client.pid,9001);assert.equal(client.exited,false);await assert.rejects(client.shutdown());
  child.emit('exit',1,null);assert.equal(client.exited,true);
});
