import test from 'node:test';
import assert from 'node:assert/strict';
import { LifecycleIntent, orderlyShutdown, type ShutdownBoundary } from '../../src/main/lifecycle.js';
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
