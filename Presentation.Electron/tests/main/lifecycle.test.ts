import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { administrationCli, startupAdoptionCli, startupAdoptionBlocksInteraction, LifecycleIntent, StopExitIntent, SHUTDOWN_DEADLINE_MS, orderlyShutdown, withinDeadline, reportStartupResult, reportStartupWithContinuation, type ShutdownBoundary } from '../../src/main/lifecycle.js';
import { ManagerController } from '../../src/main/controller.js';
import { snapshot } from './fixtures.js';

test('declining or refusing startup continuation retains the original mixed login summary',async()=>{
  for(const offered of [true,false]){
    const events:string[]=[];
    const result={outcome:'Partial' as const,code:'None',detail:'Iniciados: 1. Administrativos ignorados: 1.',correlationId:null};
    await reportStartupWithContinuation(result,offered,async()=>{events.push('offer');return false;},{show:()=>events.push('show'),warn:async value=>{events.push(value.detail!);}});
    assert.deepEqual(events,offered?['offer','show',result.detail]:['show',result.detail]);
  }
  const events:string[]=[];
  await reportStartupWithContinuation({outcome:'Partial',code:'None',detail:null,correlationId:null},true,async()=>true,{show:()=>events.push('show'),warn:async()=>{events.push('warn');}});
  assert.deepEqual(events,[],'An acknowledged replacement owns subsequent reporting');
});

test('partial, failed and unknown startup reports show the hidden host and await the named native warning',async()=>{
  const events:string[]=[];
  for(const outcome of ['Partial','Failed','Unknown','Confirmed'] as const){
    const result={outcome,code:'AccessDenied',detail:'Primeiro: acesso negado. Segundo iniciado.',correlationId:null};
    await reportStartupResult(result,{show:()=>events.push('show'),warn:async actual=>{assert.equal(actual,result);events.push(actual.detail!);}});
  }
  assert.deepEqual(events,Array.from({length:3},()=>['show','Primeiro: acesso negado. Segundo iniciado.']).flat());
  let dismiss!:()=>void,completed=false,visible=false;
  const pending=reportStartupResult({outcome:'Partial',code:'AccessDenied',detail:'Primeiro: acesso negado.',correlationId:null},{show:()=>{visible=true;},warn:()=>new Promise<void>(resolve=>{dismiss=resolve;})}).then(()=>{completed=true;});
  assert.equal(visible,true);assert.equal(completed,false);dismiss();await pending;assert.equal(completed,true);
});

test('administrative CLI accepts foreground once and rejects login/background or duplicate requests',()=>{
  assert.equal(administrationCli(['manager.exe']),'none');assert.equal(administrationCli(['manager.exe','--request-administrator']),'request');
  for(const args of [['--request-administrator','--login'],['--background','--request-administrator'],['--request-administrator','--request-administrator']])assert.equal(administrationCli(args),'invalid');
});
test('adoption descriptor selects pending intent and rejects generic login, duplicates and malformed authority hints',()=>{
  const descriptor=`11111111-1111-1111-1111-111111111111|${'a'.repeat(64)}|10|100|${'b'.repeat(64)}`;
  assert.deepEqual(startupAdoptionCli([`--startup-adoption=${descriptor}`,'--foreground']),{descriptor,invalid:false});
  assert.deepEqual(startupAdoptionCli(['manager.exe']),{descriptor:null,invalid:false});
  for(const argv of [['--startup-adoption'],['--startup-adoption=forged'],[`--startup-adoption=${descriptor}`,'--login'],[`--startup-adoption=${descriptor}`,'--background'],[`--startup-adoption=${descriptor}`,`--startup-adoption=${descriptor}`]])assert.deepEqual(startupAdoptionCli(argv),{descriptor:null,invalid:true});
});
test('completed unknown adoption opens explicit recovery while still-running adoption blocks it',async()=>{
  const calls:string[]=[];const controller=new ManagerController({request:async<T>(action:string)=>{calls.push(action);return {outcome:'Confirmed',code:'None',detail:null,correlationId:null} as T;}},{files:async()=>[],confirm:async()=>true,alive:()=>true,placement:()=>snapshot().settings.window},()=>{});
  controller.setSnapshot({...snapshot(),effectsAvailable:false,issues:[{code:'PendingOperation',detail:'Continuação incerta.',scriptId:null}]});controller.setConnection('ready');
  const pending={outcome:'Pending' as const,code:'PendingOperation',detail:null,correlationId:null};
  controller.setAdministrationPending(startupAdoptionBlocksInteraction(pending));await assert.rejects(controller.recoverPending());assert.deepEqual(calls,[]);
  controller.setAdministrationPending(startupAdoptionBlocksInteraction({...pending,outcome:'Unknown'}));
  assert.equal((await controller.recoverPending()).outcome,'Confirmed');assert.deepEqual(calls,['recoverPending']);assert.equal(controller.state.snapshot?.effectsAvailable,false);
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
  const lifecycle=new LifecycleIntent(false);lifecycle.requestLogin();assert.equal(lifecycle.shouldShowAtStartup(snapshot().settings,true),true);
});

test('start in tray applies to initial manual and login launches, retaining legacy login visibility when disabled',()=>{
  for(const initialLogin of [false,true])for(const showWindowAtLogin of [false,true]){
    const lifecycle=new LifecycleIntent(initialLogin);
    const settings={...snapshot().settings,showWindowAtLogin,startInTray:true};
    assert.equal(lifecycle.shouldShowAtStartup(settings,true),false);
    assert.equal(lifecycle.shouldShowAtStartup({...settings,startInTray:false},true),!initialLogin||showWindowAtLogin);
    lifecycle.requestLogin();assert.equal(lifecycle.shouldShowAtStartup(settings,true),false);
  }
});

test('explicit foreground handoff starts visibly without becoming a login request',()=>{
  const lifecycle=new LifecycleIntent(false,true);
  assert.equal(lifecycle.shouldShowAtStartup({...snapshot().settings,startInTray:true},true),true);
  assert.equal(lifecycle.loginRequested,false);assert.equal(lifecycle.takeLogin(),false);
});

test('actual committed-host launch arguments reopen the administrative replacement in foreground', {skip:!process.env.AHK_FOREGROUND_ARGUMENTS_INPUT},()=>{
  const argv=fs.readFileSync(process.env.AHK_FOREGROUND_ARGUMENTS_INPUT!,'utf8').trim().split(/\r?\n/);
  assert.deepEqual(argv,['--foreground']);assert.equal(administrationCli(argv),'none');
  const lifecycle=new LifecycleIntent(argv.includes('--login'),argv.includes('--foreground'));
  assert.equal(lifecycle.shouldShowAtStartup({...snapshot().settings,startInTray:true},true),true);
  assert.equal(lifecycle.takeLogin(),false);
});

test('real corrupt-journal snapshot forces recovery visibility without exposing ordinary script issues', {skip:!process.env.AHK_STARTUP_RECOVERY_INPUT},()=>{
  const state=JSON.parse(fs.readFileSync(process.env.AHK_STARTUP_RECOVERY_INPUT!,'utf8')) as ReturnType<typeof snapshot>;
  assert.equal(state.readOnly,false);assert.equal(state.effectsAvailable,false);assert.equal(state.settings.startInTray,true);
  assert.ok(state.issues.some(issue=>issue.code==='PendingOperation'&&issue.scriptId===null));
  const lifecycle=new LifecycleIntent(false);
  assert.equal(lifecycle.shouldShowAtStartup(state.settings,true,state.readOnly,state.issues),true);
  assert.equal(lifecycle.shouldShowAtStartup(state.settings,true,false,[{code:'PendingOperation',scriptId:snapshot().scripts[0]!.id,detail:null}]),false);
  assert.equal(lifecycle.shouldShowAtStartup(state.settings,true,false,[{code:'EffectOwnerUnavailable',scriptId:null,detail:null}]),false);
});

test('explicit reactivation and tray preferences override start in tray during either startup mode',()=>{
  const settings={...snapshot().settings,startInTray:true};
  for(const initialLogin of [false,true])for(const activation of ['open','preferences']){
    const lifecycle=new LifecycleIntent(initialLogin);
    if(activation==='preferences')lifecycle.requestPreferences();else lifecycle.requestExplicitActivation();
    assert.equal(lifecycle.shouldShowAtStartup(settings,true),true);
    lifecycle.requestLogin();assert.equal(lifecycle.shouldShowAtStartup(settings,true),true);
  }
});

test('missing settings, recovery failures and unavailable tray keep hidden-start requests accessible',()=>{
  for(const initialLogin of [false,true]){
    const lifecycle=new LifecycleIntent(initialLogin);const settings={...snapshot().settings,startInTray:true};
    assert.equal(lifecycle.shouldShowAtStartup(settings,false),true);
    assert.equal(lifecycle.shouldShowAtStartup(undefined,true),true);
    assert.equal(lifecycle.shouldShowAtStartup(null,true),true);
    assert.equal(lifecycle.shouldShowAtStartup(settings,true,true),true);
  }
});

test('saved maximized placement stays hidden and is retained until the window is actually shown',()=>{
  const lifecycle=new LifecycleIntent(false);const events:string[]=[];let visible=false,maximized=false;
  const window={isMinimized:()=>false,restore:()=>events.push('restore'),show:()=>{visible=true;events.push('show');},maximize:()=>{visible=true;maximized=true;events.push('maximize');},focus:()=>events.push('focus')};
  lifecycle.deferMaximizedRestore(true);
  if(lifecycle.shouldShowAtStartup({...snapshot().settings,startInTray:true},true))lifecycle.showWindow(window);
  assert.equal(visible,false);assert.deepEqual(events,[]);assert.equal(maximized||lifecycle.pendingMaximizedRestore,true);
  lifecycle.requestExplicitActivation();lifecycle.showWindow(window);
  assert.equal(visible,true);assert.equal(maximized,true);assert.deepEqual(events,['show','maximize','focus']);assert.equal(lifecycle.pendingMaximizedRestore,false);
  maximized=false;events.length=0;lifecycle.showWindow(window);
  assert.equal(maximized,false);assert.deepEqual(events,['show','focus']);
});

test('show restores a minimized window before focusing it without forcing maximized placement',()=>{
  const events:string[]=[];const lifecycle=new LifecycleIntent(true);lifecycle.deferMaximizedRestore(false);
  lifecycle.showWindow({isMinimized:()=>true,restore:()=>events.push('restore'),show:()=>events.push('show'),maximize:()=>events.push('maximize'),focus:()=>events.push('focus')});
  assert.deepEqual(events,['restore','show','focus']);
});
test('explicit activation arriving during awaited login wins over hide-at-login',()=>{
  const lifecycle=new LifecycleIntent(true);assert.equal(lifecycle.shouldShowAtStartup(snapshot().settings,true),false);assert.equal(lifecycle.takeLogin(),true);
  lifecycle.requestExplicitActivation();assert.equal(lifecycle.shouldShowAtStartup(snapshot().settings,true),true);assert.equal(lifecycle.takeLogin(),false);
});
test('tray failure keeps login window accessible and makes close exit',()=>{
  const lifecycle=new LifecycleIntent(true);assert.equal(lifecycle.shouldShowAtStartup(snapshot().settings,false),true);assert.equal(lifecycle.closeAction('HideToTray',false),'exit');
  assert.equal(lifecycle.closeAction('HideToTray',true),'hide');assert.equal(lifecycle.closeAction('Exit',true),'exit');
});
test('repeated login activations are dispatched once and cannot reset explicit intent',()=>{
  const lifecycle=new LifecycleIntent(true);assert.equal(lifecycle.takeLogin(),true);lifecycle.requestExplicitActivation();lifecycle.requestLogin();
  assert.equal(lifecycle.takeLogin(),false);assert.equal(lifecycle.shouldShowAtStartup(snapshot().settings,true),true);
});
test('Preferences requested from tray survives startup until a trusted document is ready',()=>{
  const lifecycle=new LifecycleIntent(true);lifecycle.requestPreferences();assert.equal(lifecycle.takePreferences(false),false);
  assert.equal(lifecycle.shouldShowAtStartup(snapshot().settings,true),true);assert.equal(lifecycle.takePreferences(true),true);assert.equal(lifecycle.takePreferences(true),false);
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
