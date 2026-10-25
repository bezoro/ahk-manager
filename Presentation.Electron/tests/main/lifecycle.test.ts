import test from 'node:test';
import assert from 'node:assert/strict';
import { administrationCli, LifecycleIntent, StopExitIntent, SHUTDOWN_DEADLINE_MS, orderlyShutdown, withinDeadline, type ShutdownBoundary } from '../../src/main/lifecycle.js';
import { ManagerController } from '../../src/main/controller.js';
import { snapshot } from './fixtures.js';

test('administrative CLI accepts foreground once and rejects login/background or duplicate requests',()=>{
  assert.equal(administrationCli(['manager.exe']),'none');assert.equal(administrationCli(['manager.exe','--request-administrator']),'request');
  for(const args of [['--request-administrator','--login'],['--background','--request-administrator'],['--request-administrator','--request-administrator']])assert.equal(administrationCli(args),'invalid');
});

test('keep-scripts orderly exit never prepares or performs a Stop',async()=>{
  const actions:string[]=[];const controller=new ManagerController({request:async<T>(action:string)=>{actions.push(action);return {} as T;}},{files:async()=>[],confirm:async()=>{throw new Error('No consent in keep-scripts exit');},alive:()=>true,placement:()=>snapshot().settings.window},()=>{});controller.setSnapshot(snapshot());controller.setConnection('ready');
  await orderlyShutdown({drain:()=>controller.drain(),backendPresent:()=>true,backendExited:()=>false,persistPlacement:()=>controller.savePlacement(),shutdownBackend:async()=>{actions.push('shutdown');},reportPlacementFailure:()=>{}});
  assert.deepEqual(actions,['saveSettings','shutdown']);
});
test('repeated stop/close/OS quit cancellation cannot become implicit exit or duplicate shutdown',()=>{
  const intent=new StopExitIntent();assert.equal(intent.begin(),true);assert.equal(intent.begin(),false);intent.cancel();intent.cancel();assert.equal(intent.complete(true),false);assert.equal(intent.active,false);
  assert.equal(intent.begin(),true);assert.equal(intent.complete(false),false);assert.equal(intent.begin(),true);assert.equal(intent.complete(true),true);assert.equal(intent.complete(true),false);
});
test('cancellation after all-stopped receipt but before quit transition still vetoes exit',()=>{
  const intent=new StopExitIntent();intent.begin();intent.cancel();assert.equal(intent.complete(true),false);
});
test('final observation and orderly shutdown use independent bounded phases, with late results suppressed',async()=>{
  for(const budget of [30000,SHUTDOWN_DEADLINE_MS]){
    let expire!:()=>void,finish!:(value:boolean)=>void;let cleared=false;
    const phase=withinDeadline(()=>new Promise<boolean>(resolve=>finish=resolve),budget,(fn,ms)=>{assert.equal(ms,budget);expire=fn;return ()=>{cleared=true;};});
    const rejected=assert.rejects(phase,/DeadlineExceeded/);expire();await rejected;finish(true);assert.equal(cleared,true);
  }
  assert.equal(SHUTDOWN_DEADLINE_MS,15000);
});
test('normal explicit launch remains visible even when login later redirects into the primary instance',()=>{
  const lifecycle=new LifecycleIntent(false);lifecycle.requestLogin();assert.equal(lifecycle.shouldShowAtStartup(false,true),true);
});
test('explicit activation arriving during awaited login wins over hide-at-login',()=>{
  const lifecycle=new LifecycleIntent(true);assert.equal(lifecycle.shouldShowAtStartup(false,true),false);assert.equal(lifecycle.takeLogin(),true);
  lifecycle.requestExplicitActivation();assert.equal(lifecycle.shouldShowAtStartup(false,true),true);assert.equal(lifecycle.takeLogin(),false);
});
test('tray failure keeps login window accessible and makes close exit',()=>{
  const lifecycle=new LifecycleIntent(true);assert.equal(lifecycle.shouldShowAtStartup(false,false),true);assert.equal(lifecycle.closeAction('HideToTray',false),'exit');
  assert.equal(lifecycle.closeAction('HideToTray',true),'hide');assert.equal(lifecycle.closeAction('Exit',true),'exit');
});
test('repeated login activations are dispatched once and cannot reset explicit intent',()=>{
  const lifecycle=new LifecycleIntent(true);assert.equal(lifecycle.takeLogin(),true);lifecycle.requestExplicitActivation();lifecycle.requestLogin();
  assert.equal(lifecycle.takeLogin(),false);assert.equal(lifecycle.shouldShowAtStartup(false,true),true);
});
test('Preferences requested from tray survives startup until a trusted document is ready',()=>{
  const lifecycle=new LifecycleIntent(true);lifecycle.requestPreferences();assert.equal(lifecycle.takePreferences(false),false);
  assert.equal(lifecycle.shouldShowAtStartup(false,true),true);assert.equal(lifecycle.takePreferences(true),true);assert.equal(lifecycle.takePreferences(true),false);
});
test('Exit succeeds after an observed backend crash even when a stale snapshot exists',async()=>{
  const calls:string[]=[];const staleSnapshot={revision:'12',settings:{window:{x:100}}};
  await orderlyShutdown({drain:async()=>{calls.push('drain');},backendPresent:()=>true,backendExited:()=>true,
    persistPlacement:async()=>{calls.push('persist');throw new Error(`sticky unavailable backend for snapshot ${staleSnapshot.revision}`);},
    shutdownBackend:async()=>{calls.push('shutdown');},reportPlacementFailure:()=>calls.push('diagnostic')});
  assert.deepEqual(calls,['drain']);
});
test('placement failure on a live backend is best effort and still attempts observed shutdown',async()=>{
  const calls:string[]=[];let exited=false;
  await orderlyShutdown({drain:async()=>{calls.push('drain');},backendPresent:()=>true,backendExited:()=>exited,
    persistPlacement:async()=>{calls.push('persist');throw new Error('storage failed');},
    shutdownBackend:async()=>{calls.push('shutdown');exited=true;},reportPlacementFailure:()=>calls.push('diagnostic')});
  assert.equal(exited,true);assert.deepEqual(calls,['drain','persist','diagnostic','shutdown']);
});
test('alive unresponsive shutdown remains unknown and cannot become a success receipt',async()=>{
  let completed=false;const boundary:ShutdownBoundary={drain:async()=>{},backendPresent:()=>true,backendExited:()=>false,
    persistPlacement:async()=>{throw new Error('unavailable');},shutdownBackend:async()=>{throw new Error('unknown shutdown deadline');},reportPlacementFailure:()=>{}};
  await assert.rejects(orderlyShutdown(boundary).then(()=>{completed=true;}),/unknown shutdown/);assert.equal(completed,false);
});
