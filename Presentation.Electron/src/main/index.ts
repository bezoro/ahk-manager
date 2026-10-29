import { app, BrowserWindow, clipboard, dialog, ipcMain, Menu, nativeImage, nativeTheme, protocol, net, screen, session, shell, systemPreferences, Tray } from 'electron';
import { scriptFileStamp } from './script-files.js';
import path from 'node:path';
import fs from 'node:fs/promises';
import os from 'node:os';
import { AsyncLocalStorage } from 'node:async_hooks';
import { BackendClient } from './backend.js';
import { ManagerController } from './controller.js';
import { administrationCli, startupAdoptionCli, startupAdoptionBlocksInteraction, LifecycleIntent, StopExitIntent, SHUTDOWN_DEADLINE_MS, orderlyShutdown, reportStartupWithContinuation, configuredLoginElevation } from './lifecycle.js';
import { AdministrationPreflight, ElevationCoordinator, administrationDocumentReady } from './elevation.js';
import { createDiagnostics, type PageDiagnostics } from './diagnostics.js';
import {hostText as t} from '../shared/host-en-US.js';
import { APP_URL, assetFromUrl, CSP, DocumentGuard, packagedBackend, type DocumentLease } from './security.js';
import { capturePlacement, restorePlacement } from './placement.js';
import type { AdministrationUiGuard, ElevationStatus, ManagerSnapshot, OperationResult, StartupContinuationOffer, ThemeState, WindowPlacement } from '../shared/contracts.js';
import { activityQuery, array, assertActivityQuery, assertMetadata, assertOperation, assertSelection, assertSettings, bool, decimal, guid, metadata, nullable, oneOf, operation, requireValid, selection, shape, text } from '../shared/validation.js';
import { UpdateService } from './update-service.js';
import { UpdateStore, type CachedInstaller } from './update-store.js';
import { GitHubUpdateClient, compareVersions } from './github-updates.js';
import { createElectronUpdateFetch } from './electron-update-fetch.js';
import { configureUpdateNetwork } from './update-network-policy.js';
import { downloadRelease, verifyCachedInstallerSignature } from './electron-update-download.js';
import { prepareUpdate, updateRecoveryStatus, type PreparedUpdate } from './update-handoff.js';
import { shutdownAndCommitUpdate } from './update-shutdown.js';

app.commandLine.appendSwitch('lang', 'en-US');
protocol.registerSchemesAsPrivileged([{scheme:'ahk-manager',privileges:{standard:true,secure:true,supportFetchAPI:true,corsEnabled:true}}]);
app.setName(t.title);
app.setAppUserModelId('Bezoro.AhkManager.Electron');
// Chromium storage never opens or copies a framework's previous operational profile.
app.setPath('userData',path.join(app.getPath('appData'),'Bezoro','AhkManagerElectron','Chromium'));
let window:BrowserWindow|null=null;
let guard:DocumentGuard|null=null;
let backend:BackendClient|null=null;
let controller:ManagerController|null=null;
let tray:Tray|null=null;
let trayRegistered=false;
let material:ThemeState['material']='solid';
let diagnostics:PageDiagnostics|null=null;
let diagnosticSelection:string|null=null;
let ready=false;
let rendererReady=false;
let documentLease:DocumentLease|null=null;
const initialAdministration=administrationCli(process.argv);
const initialAdoption=startupAdoptionCli(process.argv);
const quietAdoption=initialAdoption.descriptor!==null&&process.argv.includes('--background');
let adoptionPending=initialAdoption.descriptor!==null||initialAdoption.invalid;
let continuationOffer:StartupContinuationOffer|null=null;
let administrativeCliPending=initialAdministration==='request';
const lifecycle=new LifecycleIntent(!adoptionPending&&initialAdministration==='none'&&process.argv.includes('--login'),(adoptionPending&&!quietAdoption)||initialAdministration!=='none'||process.argv.includes('--foreground'),quietAdoption);
const preflight=new AdministrationPreflight();
let administration:ElevationCoordinator|null=null;
let administrationLease:DocumentLease|null=null;
let quietStartupTransfer=false;
const stopExit=new StopExitIntent();
let stopExitProgress={done:0,total:0};
let quitting=false;
let quitComplete=false;
let updates:UpdateService|null=null;
let updateStore:UpdateStore|null=null;
let updateApplying=false;
let updateShutdownInFlight:Promise<void>|null=null;
const documentScope=new AsyncLocalStorage<()=>boolean>();
const alive=()=>!quitting&&(documentScope.getStore()?.()??(window!==null&&!window.isDestroyed()));
const single=app.requestSingleInstanceLock();
if(!single)app.quit();
else {
  app.on('second-instance',(_event,argv)=>{
    if(adoptionPending||argv.some(value=>value==='--startup-adoption'||value.startsWith('--startup-adoption='))){if(!argv.includes('--login')&&!argv.includes('--background')){lifecycle.requestExplicitActivation();show();}return;}
    if(initialAdoption.descriptor!==null&&argv.includes('--login'))return;
    const administrative=administrationCli(argv);
    if(administrative!=='none'){lifecycle.requestExplicitActivation();show();if(administrative==='invalid')dialog.showErrorBox(t.title,t.administrationCliInvalid);else if(ready)void requestAdministration(true);else administrativeCliPending=true;return;}
    if(argv.includes('--login')){lifecycle.requestLogin();if(ready)void runLogin();}
    else {lifecycle.requestExplicitActivation();show();}
  });
  app.on('activate',()=>{lifecycle.requestExplicitActivation();show();});
  app.on('before-quit',event=>{if(!quitComplete){event.preventDefault();void quit();}});
  app.on('window-all-closed',()=>{if(!quitting)void quit();});
  void app.whenReady().then(start).catch(error=>{if(!lifecycle.quietStartup)dialog.showErrorBox(t.title,safeError(error));quitComplete=true;app.quit();});
}
function safeError(error:unknown):string {return error instanceof Error?error.message.slice(0,4096):t.unknownError;}
function theme():ThemeState {
  const color=systemPreferences.getAccentColor();
  return {dark:nativeTheme.shouldUseDarkColors,highContrast:nativeTheme.shouldUseHighContrastColors,reducedMotion:!systemPreferences.getAnimationSettings().shouldRenderRichAnimation,accent:`#${color.slice(0,6)}`,material};
}
function updateTheme():void {
  if(!window||window.isDestroyed())return;
  const snapshot=controller?.state.snapshot;
  nativeTheme.themeSource=snapshot?.settings.theme==='Light'?'light':snapshot?.settings.theme==='Dark'?'dark':'system';
  const supportsMica=Number(os.release().split('.')[2]??0)>=22621&&!nativeTheme.shouldUseHighContrastColors;
  try{window.setBackgroundMaterial(supportsMica?'mica':'none');material=supportsMica?'mica':'solid';}catch{material='solid';}
  window.setBackgroundColor(material==='mica'?'#00000000':nativeTheme.shouldUseDarkColors?'#202020':'#f7f7f7');
  const state=theme();
  window.setTitleBarOverlay({height:44,color:material==='mica'&&!state.highContrast?'#00000000':state.dark?'#202020':'#f7f7f7',symbolColor:state.dark?'#ffffff':'#1b1b1b'});
  guard?.send('manager:theme',state);
}
function show():void {
  if(!window||window.isDestroyed()||quitting)return;
  lifecycle.showWindow(window);
  deliverPreferences();
}
function deliverPreferences():void {if(lifecycle.takePreferences(ready&&rendererReady&&!!guard?.connected))guard?.send('manager:preferences',null);}
function showPreferences():void {if(administration?.active)return;lifecycle.requestPreferences();show();deliverPreferences();}
function placement():WindowPlacement {
  if(!window||window.isDestroyed())return controller?.state.snapshot?.settings.window??{x:100,y:100,width:1200,height:780,maximized:false,dpi:96};
  const maximized=window.isMaximized()||lifecycle.pendingMaximizedRestore;
  const bounds=window.isMaximized()?window.getNormalBounds():window.getBounds();
  const physical=screen.dipToScreenRect(window,bounds);
  return {...physical,maximized,dpi:screen.getDisplayMatching(bounds).scaleFactor*96};
}
async function confirm(title:string,detail:string):Promise<boolean> {
  if(!alive()||!window)return false;
  const response=await dialog.showMessageBox(window,{type:'question',title,message:title,detail,buttons:[t.cancel,t.confirm],defaultId:0,cancelId:0,noLink:true});
  return response.response===1&&alive();
}
async function pick(kind:'scripts'|'locate'|'folder'|'interpreter'|'editor'|'import'|'migration'):Promise<string[]> {
  if(!alive()||!window)return [];
  const directory=kind==='folder'||kind==='migration';
  const response=await dialog.showOpenDialog(window,{title:t.pickerTitles[kind],properties:directory?['openDirectory','dontAddToRecent']:kind==='scripts'?['openFile','multiSelections','dontAddToRecent']:['openFile','dontAddToRecent'],filters:directory?[]:kind==='scripts'||kind==='locate'?[{name:t.fileFilters.scripts,extensions:['ahk']}]:kind==='import'?[{name:t.fileFilters.catalog,extensions:['json']}]:[{name:t.fileFilters.applications,extensions:['exe']}]});
  if(response.canceled||!alive())return [];
  if(response.filePaths.length>4096)throw new Error(t.fileLimit);
  for(const file of response.filePaths){if(!path.isAbsolute(file)||file.includes('\0'))throw new Error(t.invalidPath);const info=await fs.stat(file);if(directory?!info.isDirectory():!info.isFile())throw new Error(t.wrongFileType);}
  return response.filePaths;
}
async function serveAssets():Promise<void> {
  const root=path.join(app.getAppPath(),'dist','renderer');
  const manifest:unknown=JSON.parse(await fs.readFile(path.join(app.getAppPath(),'dist','assets.json'),'utf8'));
  const allow=new Set(requireValid<string[]>(manifest,array(text(256),256),'asset manifest'));
  protocol.handle('ahk-manager',async request=>{
    const name=request.method==='GET'?assetFromUrl(request.url,allow):null;
    if(!name)return new Response('Forbidden',{status:403});
    const types:Record<string,string>={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml','.png':'image/png','.woff2':'font/woff2'};
    try{return new Response(await fs.readFile(path.join(root,...name.split('/'))),{headers:{'Content-Type':types[path.extname(name)]??'application/octet-stream','Content-Security-Policy':CSP,'X-Content-Type-Options':'nosniff','Cross-Origin-Resource-Policy':'same-origin','Cache-Control':'no-store'}});}catch{return new Response('Not found',{status:404});}
  });
}
function lockSession():void {
  const target=session.defaultSession;
  target.setPermissionRequestHandler((_contents,_permission,callback)=>callback(false));
  target.setPermissionCheckHandler(()=>false);
  target.setDevicePermissionHandler(()=>false);
  target.on('will-download',event=>event.preventDefault());
  target.webRequest.onBeforeRequest((details,callback)=>callback({cancel:!details.url.startsWith('ahk-manager://app/')}));
}
async function configureUpdates():Promise<void> {
  // A dedicated Electron session retains OS proxy support and cannot grant renderer access.
  const network=session.fromPartition('electron-updater',{cache:false});
  configureUpdateNetwork(network);
  const root=path.join(process.env.LOCALAPPDATA??path.join(app.getPath('home'),'AppData','Local'),'Bezoro','AhkManagerElectron','Updates');
  updateStore=new UpdateStore(root);
  const client=new GitHubUpdateClient(createElectronUpdateFetch(network,options=>net.request(options)));
  updates=new UpdateService({
    version:app.getVersion(),store:updateStore,releases:channel=>client.releases(channel),resolve:id=>client.resolve(id),download:downloadRelease,
    preflight:(operationId,phase)=>{
      const lease=documentLease;
      return preflight.check(operationId,phase,request=>guard?.send('manager:administrationPreflight',request),()=>!!lease&&!!guard?.current(lease)&&!!window?.isFocused()&&!window.isDestroyed()&&!quitting&&!updateApplying&&!updateShutdownInFlight&&!adoptionPending&&!administration?.active&&!stopExit.active&&!controller?.state.busy&&!controller?.state.administrationPending);
    },
    confirm:release=>confirm(compareVersions(release.version,app.getVersion())<0?'Return to version '+release.version+'?':'Update to version '+release.version+'?','The manager will close and reopen. Running scripts will keep running, and your settings will be preserved.'),
    apply:applyUpdate,changed:state=>guard?.send('manager:updates',state),recovery:()=>updateRecoveryStatus(updateStore!),
  });
  await updates.initialize();
  void updates.check();
  const interval=setInterval(()=>{if(!quitting&&!updateApplying)void updates?.check();},15*60*1000);interval.unref();
}
async function applyUpdate(operationId:string,target:CachedInstaller):Promise<void> {
  if(!updateStore||!window||updateApplying||updateShutdownInFlight||quitting||adoptionPending||administration?.active||stopExit.active)throw new Error('Wait for manager operations to finish before updating.');
  updateApplying=true;controller?.setAdministrationPending(true);
  let prepared:PreparedUpdate|null=null;
  let shutdownStarted=false;
  try {
    await verifyCachedInstallerSignature(target.installerPath);
    const created=app.getAppMetrics().find(metric=>metric.pid===process.pid)?.creationTime;
    if(!created||!Number.isFinite(created))throw new Error('The manager session could not be verified for this update.');
    prepared=await prepareUpdate(updateStore,operationId,target,path.dirname(app.getPath('exe')),new Date(created).toISOString());
    shutdownStarted=true;
    await shutdownAndCommitUpdate({
      drain:async()=>{await controller?.drain();},backendPresent:()=>backend!==null,backendExited:()=>backend?.exited??true,
      persistPlacement:async()=>{await controller?.savePlacement(true);},shutdownBackend:async()=>{
        if(!backend)return;
        const closing=backend.shutdown().then(()=>{});updateShutdownInFlight=closing;
        try{await closing;}finally{if(updateShutdownInFlight===closing)updateShutdownInFlight=null;}
      },
      reportPlacementFailure:()=>{},
    },SHUTDOWN_DEADLINE_MS,()=>prepared!.commit());
    quitting=true;tray?.destroy();quitComplete=true;app.quit();
  } catch(error) {
    let cancellationError:unknown;
    try{await prepared?.cancel();}catch(cancelError){cancellationError=cancelError;}
    finally{
      updateApplying=false;controller?.setAdministrationPending(false);
      if(shutdownStarted&&backend)controller?.setConnection('failed','The update was cancelled without confirmed shutdown. Wait and reconnect the backend before continuing.');
    }
    if(cancellationError)throw new Error(safeError(error)+' The update cancellation could not be recorded.',{cause:cancellationError});
    throw error;
  }
}
async function start():Promise<void> {
  if(initialAdoption.invalid)throw new Error(t.adoptionInvalid);
  if(process.platform!=='win32'||!app.isPackaged)throw new Error(t.windowsPackagedRequired);
  await serveAssets();lockSession();
  const supportsMica=Number(os.release().split('.')[2]??0)>=22621;
  window=new BrowserWindow({width:1200,height:780,minWidth:760,minHeight:560,show:false,title:t.title,backgroundColor:'#00000000',titleBarStyle:'hidden',titleBarOverlay:{height:44},autoHideMenuBar:true,webPreferences:{preload:path.join(__dirname,'../preload/index.cjs'),nodeIntegration:false,contextIsolation:true,sandbox:true,webSecurity:true,allowRunningInsecureContent:false,devTools:false,spellcheck:false}});
  diagnostics=createDiagnostics(process.argv,{
    capture:async()=>{if(!window||window.isDestroyed()||window.webContents.getURL()!==APP_URL)throw new Error('Diagnostic document unavailable');const size=window.getContentBounds();if(size.width>4096||size.height>4096)throw new Error('Diagnostic viewport exceeds bound');return (await window.webContents.capturePage()).toPNG();},
    summary:()=>({rendererPid:window?.webContents.getOSProcessId(),url:window?.webContents.getURL(),viewport:window?.getContentBounds(),material,theme:controller?.state.snapshot?.settings.theme??'System',revision:controller?.state.snapshot?.revision??null,selectedId:diagnosticSelection,scriptCount:controller?.state.snapshot?.scripts.length??0}),
  });
  if(diagnostics){
    diagnostics.record('runtime',{electron:process.versions.electron,chromium:process.versions.chrome,node:process.versions.node,disableGpu:app.commandLine.hasSwitch('disable-gpu'),disableDirectComposition:app.commandLine.hasSwitch('disable-direct-composition')});
    window.webContents.on('console-message',details=>diagnostics?.record('console',{level:details.level,message:details.message,line:details.lineNumber}));
    window.webContents.on('preload-error',(_event,_preload,error)=>diagnostics?.record('preload-error',{message:error.message}));
    window.webContents.on('did-fail-load',(_event,code,description,_url,mainFrame)=>diagnostics?.record('did-fail-load',{code,description,mainFrame}));
    window.webContents.on('render-process-gone',(_event,details)=>diagnostics?.record('render-process-gone',{reason:details.reason,exitCode:details.exitCode}));
    window.webContents.on('did-finish-load',()=>diagnostics?.record('did-finish-load'));
    window.on('unresponsive',()=>diagnostics?.record('unresponsive'));window.on('responsive',()=>diagnostics?.record('responsive'));
    app.on('child-process-gone',(_event,details)=>diagnostics?.record('child-process-gone',{type:details.type,reason:details.reason,exitCode:details.exitCode}));
    app.once('will-quit',()=>diagnostics?.close());
  }
  Menu.setApplicationMenu(null);
  if(supportsMica&&!nativeTheme.shouldUseHighContrastColors){try{window.setBackgroundMaterial('mica');material='mica';}catch{material='solid';}}
  if(material==='solid')window.setBackgroundColor(nativeTheme.shouldUseDarkColors?'#202020':'#f7f7f7');
  guard=new DocumentGuard(window.webContents);
  window.webContents.setWindowOpenHandler(()=>({action:'deny'}));
  window.webContents.on('will-navigate',event=>event.preventDefault());
  window.webContents.on('will-frame-navigate',event=>event.preventDefault());
  window.webContents.on('will-attach-webview',event=>event.preventDefault());
  window.webContents.on('did-start-navigation',(_event,_url,isInPlace,isMainFrame)=>{if(isMainFrame&&!isInPlace){rendererReady=false;documentLease=null;preflight.invalidate();guard?.invalidate();controller?.updateSelection(null);}});
  window.webContents.on('render-process-gone',()=>{rendererReady=false;documentLease=null;preflight.invalidate();guard?.invalidate();controller?.updateSelection(null);});
  window.webContents.on('did-finish-load',deliverPreferences);
  window.on('close',event=>{if(quitComplete)return;event.preventDefault();void closeWindow();});
  nativeTheme.on('updated',updateTheme);
  systemPreferences.on('accent-color-changed',updateTheme);
  const native={files:pick,confirm,placement,alive,fileStamp:scriptFileStamp,trash:(target:string)=>shell.trashItem(path.win32.normalize(target))};
  controller=new ManagerController({request:()=>Promise.reject(new Error('Backend starting'))},native,state=>{guard?.send('manager:state',state);updateTheme();updateTray();});
  administration=new ElevationCoordinator({
    eligible:()=>!adoptionPending&&!quitting&&!stopExit.active&&controller?.state.connection==='ready'&&controller.state.authorityMode==='Normal'&&!controller.state.busy&&(!quietStartupTransfer||(lifecycle.quietStartup&&configuredLoginElevation(controller.state.snapshot?.settings)&&controller.state.snapshot?.readOnly===false&&controller.state.snapshot.effectsAvailable))&&!!backend?.ready,
    confirm:()=>confirm(t.administrationTitle,t.administrationDetail),
    preflight:(operationId,phase)=>{const lease=administrationLease;return preflight.check(operationId,phase,request=>guard?.send('manager:administrationPreflight',request),()=>administrationDocumentReady({currentDocument:!!lease&&!!guard?.current(lease),windowAlive:!!window&&!window.isDestroyed(),focused:window?.isFocused()===true,quietStartup:quietStartupTransfer&&lifecycle.quietStartup&&configuredLoginElevation(controller?.state.snapshot?.settings)}));},
    quiesce:value=>controller?.setAdministrationPending(value),
    drain:async()=>{await controller?.drain();},persistPlacement:async()=>{await controller?.savePlacement(true);},
    request:(action,payload,timeoutMs)=>{if(!backend)throw new Error(t.backendUnavailable);return backend.request<ElevationStatus>(action,payload,timeoutMs);},
    quitAfterAcknowledgement:()=>quit(true),
  });
  await configureUpdates();
  registerIpc();
  await window.loadURL(APP_URL);
  createTray();updateTheme();
  await startBackend();
  const saved=controller.state.snapshot?.settings.window;
  if(saved){const dip=screen.screenToDipRect(window,{x:saved.x,y:saved.y,width:saved.width,height:saved.height});window.setBounds(restorePlacement({...saved,...dip,dpi:96},screen.getAllDisplays()));lifecycle.deferMaximizedRestore(saved.maximized);if(window.isVisible())show();}
  ready=true;
  if(initialAdministration==='invalid')dialog.showErrorBox(t.title,t.administrationCliInvalid);
  const wantsAdministrator=controller.state.snapshot?.settings.launchManagerAsAdministrator===true;
  const canInitialize=!initialAdoption.descriptor&&!adoptionPending&&controller.state.connection==='ready'&&controller.state.snapshot?.readOnly===false&&controller.state.snapshot.effectsAvailable;
  let setupDeferred=false;
  if(administrativeCliPending||canInitialize&&wantsAdministrator&&controller.state.authorityMode==='Normal'&&(!lifecycle.quietStartup||lifecycle.takeAutomaticElevation(controller.state.snapshot?.settings))){
    administrativeCliPending=false;
    const quiet=lifecycle.quietStartup;
    if(!quiet)show();
    const result=await requestAdministration(true,lifecycle.loginRequested,quiet);
    if(result.outcome==='Confirmed'){
      if(quitting||quitComplete)return;
    }else{
      setupDeferred=true;
      if(window&&!quiet)await dialog.showMessageBox(window,{type:'warning',title:t.administrationTitle,message:t.administrationSetupDeferred,detail:result.detail??t.loginFailure(result.code),buttons:[t.confirm],defaultId:0,cancelId:0,noLink:true});
    }
  }
  if(canInitialize&&!setupDeferred&&backend?.ready){
    const result=await backend.request<OperationResult>('initializeStartup',{});
    if(result.outcome!=='Confirmed'&&window&&!lifecycle.quietStartup){show();await dialog.showMessageBox(window,{type:'warning',title:t.loginResultTitle,message:t.startupSetupIncomplete,detail:result.detail??t.loginFailure(result.code),buttons:[t.confirm],defaultId:0,cancelId:0,noLink:true});}
  }
  await runLogin();
  if(lifecycle.shouldShowAtStartup(controller.state.snapshot?.settings,trayRegistered,controller.state.connection!=='ready'||controller.state.snapshot?.readOnly===true,controller.state.snapshot?.issues))show();
  if(window.isVisible())diagnostics?.initialReady();
}
async function startBackend(): Promise<void> {
  if (administration?.active) throw new Error(t.administrationPending);
  if (stopExit.active) throw new Error(t.stopExitBusy);
  if (quitting) throw new Error(t.closing);
  if (!controller) throw new Error('Controller unavailable');
  if (backend && !backend.exited) throw new Error(t.backendStillAlive(backend.pid));
  backend = new BackendClient({
    executable: packagedBackend(path.dirname(process.execPath)),
    hostExecutable: process.execPath,
    ...(initialAdoption.descriptor ? { startupAdoption: initialAdoption.descriptor } : {}),
  });
  if (adoptionPending) controller.setAdministrationPending(true);
  controller.setAuthority(backend);
  controller.setConnection('starting');
  backend.on('snapshot', (snapshot: ManagerSnapshot) => controller?.setSnapshot(snapshot));
  backend.on('unavailable', (error: Error) => {
    if (!quitting) controller?.setConnection('failed', error.message);
  });
  try {
    await backend.start();
    controller.setHostAuthority(backend.authorityMode);
    if (backend.profileRoot && !lifecycle.loginRequested && !lifecycle.quietStartup) {
      const exists = await fs.access(path.join(backend.profileRoot, 'settings.json')).then(
        () => true,
        () => false,
      );
      if (!exists && window) {
        const choice = await dialog.showMessageBox(window, {
          type: 'question',
          title: t.welcomeTitle,
          message: t.welcomeQuestion,
          detail: t.welcomeDetail,
          buttons: [t.newProfile, t.chooseWinUiProfile],
          defaultId: 0,
          cancelId: 0,
          noLink: true,
        });
        if (choice.response === 1) await controller.migrateProfile(true);
      }
    }
    controller.setSnapshot(await backend.request<ManagerSnapshot>('load', {}));
    controller.setConnection('ready');
    if (initialAdoption.descriptor) {
      const result = await backend.request<OperationResult>(
        'adoptStartupContinuation',
        {},
        150000,
      );
      adoptionPending = startupAdoptionBlocksInteraction(result);
      controller.setAdministrationPending(adoptionPending);
      if (!lifecycle.quietStartup) show();
      if (window && !lifecycle.quietStartup)
        await dialog.showMessageBox(window, {
          type: result.outcome === 'Confirmed' ? 'info' : 'warning',
          title: t.loginResultTitle,
          message: t.loginResultTitle,
          detail: result.detail ?? t.loginFailure(result.code),
          buttons: [t.confirm],
          defaultId: 0,
          cancelId: 0,
          noLink: true,
        });
    }
  } catch (error) {
    controller.setConnection('failed', safeError(error));
    if (!lifecycle.quietStartup) show();
  }
}
async function reportLoginResult(result: OperationResult): Promise<void> {
  try {
    continuationOffer = backend?.ready
      ? await backend.request<StartupContinuationOffer | null>('startupContinuation', {})
      : null;
  } catch {
    continuationOffer = null;
  }
  updateTray();
  await reportStartupWithContinuation(
    result,
    continuationOffer?.canResume === true &&
      (!lifecycle.quietStartup || configuredLoginElevation(controller?.state.snapshot?.settings)),
    () => requestStartupContinuation(lifecycle.quietStartup),
    {
      show,
      warn: async (value) => {
        if (window)
          await dialog.showMessageBox(window, {
            type: 'warning',
            title: t.loginResultTitle,
            message: t.loginResultTitle,
            detail: value.detail ?? t.loginFailure(value.code),
            buttons: [t.confirm],
            defaultId: 0,
            cancelId: 0,
            noLink: true,
          });
      },
    },
    lifecycle.quietStartup,
  );
}
async function checkStartupAdoption():Promise<void> {
  if(!adoptionPending||!backend?.ready||!controller||quitting)return;
  try{
    const result=await backend.request<OperationResult>('startupAdoptionStatus',{},10000);
    adoptionPending=startupAdoptionBlocksInteraction(result);controller.setAdministrationPending(adoptionPending);controller.setConnection('ready');updateTray();show();
    if(window)await dialog.showMessageBox(window,{type:result.outcome==='Confirmed'?'info':'warning',title:t.loginResultTitle,message:t.loginResultTitle,detail:result.detail??t.loginFailure(result.code),buttons:[t.confirm],defaultId:0,cancelId:0,noLink:true});
  }catch{show();dialog.showErrorBox(t.loginResultTitle,t.administrationPending);}
}
async function requestStartupContinuation(quiet = false): Promise<boolean> {
  if (adoptionPending || administration?.active || !backend?.ready || !controller || quitting)
    return false;
  if (quiet && !lifecycle.takeAutomaticElevation(controller.state.snapshot?.settings))
    return false;
  continuationOffer = await backend.request<StartupContinuationOffer | null>(
    'startupContinuation',
    {},
  );
  updateTray();
  if (!continuationOffer?.canResume || !window) return false;
  if (!quiet) {
    show();
    const choice = await dialog.showMessageBox(window, {
      type: 'warning',
      title: t.continuationTitle,
      message: t.continuationTitle,
      detail: t.continuationDetail(continuationOffer.scriptNames),
      buttons: [t.continuationContinue, t.continuationLater],
      defaultId: 1,
      cancelId: 1,
      noLink: true,
    });
    if (choice.response !== 0) return false;
  }
  const deadline = Date.now() + 3000;
  while (!documentLease && !quitting && Date.now() < deadline)
    await new Promise((resolve) => setTimeout(resolve, 25));
  administrationLease = documentLease;
  let result: OperationResult | undefined;
  quietStartupTransfer = quiet;
  try {
    result = await administration?.request(true, continuationOffer.operationId, false, quiet);
  } finally {
    quietStartupTransfer = false;
  }
  if (result && result.outcome !== 'Confirmed' && window && !quiet)
    await dialog.showMessageBox(window, {
      type: 'warning',
      title: t.loginResultTitle,
      message: t.loginResultTitle,
      detail: result.detail ?? t.loginFailure(result.code),
      buttons: [t.confirm],
      defaultId: 0,
      cancelId: 0,
      noLink: true,
    });
  return result?.outcome === 'Confirmed';
}
async function runLogin(): Promise<void> {
  if (
    initialAdoption.descriptor !== null ||
    adoptionPending ||
    administration?.active ||
    stopExit.active ||
    quitting ||
    !backend?.ready ||
    !controller ||
    !lifecycle.takeLogin()
  )
    return;
  try {
    await reportLoginResult(await controller.runLogin());
  } catch (error) {
    controller.setConnection('failed', safeError(error));
    if (!lifecycle.quietStartup) {
      show();
      dialog.showErrorBox(t.loginResultTitle, safeError(error));
    }
  }
}
async function requestAdministration(explicitCli = false, resumeLogin = false, quiet = false) {
  if (adoptionPending)
    return {
      outcome: 'CancelledBeforeDispatch' as const,
      code: 'PendingOperation',
      detail: null,
      correlationId: null,
    };
  if (administration?.processing)
    return {
      outcome: 'CancelledBeforeDispatch' as const,
      code: 'PendingOperation',
      detail: null,
      correlationId: null,
    };
  if (controller?.state.authorityMode === 'Administrator') {
    if (!quiet) show();
    return { outcome: 'Confirmed' as const, code: 'None', detail: null, correlationId: null };
  }
  if (explicitCli) {
    const deadline = Date.now() + 3000;
    while (!documentLease && !quitting && Date.now() < deadline)
      await new Promise((resolve) => setTimeout(resolve, 25));
  }
  administrationLease = documentLease;
  quietStartupTransfer = quiet;
  try {
    return (
      (await administration?.request(explicitCli, undefined, resumeLogin)) ?? {
        outcome: 'CancelledBeforeDispatch' as const,
        code: 'CapabilityUnverified',
        detail: null,
        correlationId: null,
      }
    );
  } finally {
    quietStartupTransfer = false;
  }
}
function createTray():void {
  try{
    const image=nativeImage.createFromPath(path.join(process.resourcesPath,'tray.png'));
    if(image.isEmpty())throw new Error('Tray icon unavailable');
    tray=new Tray(image);tray.setToolTip(t.title);tray.on('double-click',()=>{lifecycle.requestExplicitActivation();show();});trayRegistered=true;updateTray();
  }catch{trayRegistered=false;tray=null;}
}
function updateTray():void {
  if(!tray||tray.isDestroyed())return;
  const running=controller?.state.snapshot?.scripts.filter(s=>s.status==='Running')??[];
  tray.setContextMenu(Menu.buildFromTemplate([{label:t.continuationCheck,visible:adoptionPending,enabled:adoptionPending&&backend?.ready===true&&!quitting,click:()=>void checkStartupAdoption()},{label:t.continuationMenu,visible:continuationOffer!==null,enabled:continuationOffer?.canResume===true&&!adoptionPending&&!administration?.active&&!quitting,click:()=>void requestStartupContinuation()},{label:t.tray.open,click:()=>{lifecycle.requestExplicitActivation();show();}},{label:window?.isVisible()?t.tray.hide:t.tray.show,click:()=>{lifecycle.requestExplicitActivation();if(window?.isVisible())window.hide();else show();updateTray();}},{type:'separator'},{label:t.tray.running(running.length),submenu:running.length?running.map(s=>({label:s.friendlyName,enabled:false})):[{label:t.tray.noneRunning,enabled:false}]},{label:t.tray.preferences,enabled:!stopExit.active&&!administration?.active,click:showPreferences},{type:'separator'},{label:t.tray.exit,enabled:!stopExit.active&&!quitting,click:()=>void quit()},{label:t.tray.stopExit,enabled:!stopExit.active&&!quitting&&controller?.state.connection==='ready',click:()=>void stopScriptsAndQuit()},{label:t.tray.stopping(stopExitProgress.done,stopExitProgress.total),visible:stopExit.active,enabled:false},{label:t.tray.cancelStopExit,visible:stopExit.active,click:()=>{stopExit.cancel();updateTray();}}]));
}
async function stopScriptsAndQuit():Promise<void> {
  if(adoptionPending||administration?.active||quitting||!controller||!stopExit.begin())return;
  stopExitProgress={done:0,total:0};updateTray();
  try{
    const receipt=await controller.stopScriptsForExit({cancelled:()=>stopExit.cancelled,progress:(done,total)=>{stopExitProgress={done,total};updateTray();}});
    if(stopExit.complete(receipt.canExit)){await quit();return;}
    updateTray();show();if(window)await dialog.showMessageBox(window,{type:'warning',title:t.stopExitPartialTitle,message:t.stopExitPartialTitle,detail:t.stopExitPartial(receipt),buttons:[t.confirm],defaultId:0,cancelId:0,noLink:true});
  }catch(error){stopExit.complete(false);updateTray();show();dialog.showErrorBox(t.stopExitPartialTitle,safeError(error));}
}
async function closeWindow():Promise<void> {
  if(updateApplying||updateShutdownInFlight){show();return;}
  if(administration?.active){show();return;}
  if(stopExit.active){stopExit.cancel();updateTray();return;}
  if(quitting)return;
  try{await controller?.savePlacement();}catch(error){controller?.setConnection('failed',safeError(error));}
  if(lifecycle.closeAction(controller?.state.snapshot?.settings.closeBehavior??'Exit',trayRegistered)==='hide'){window?.hide();updateTray();}
  else await quit();
}
async function quit(administrativeAcknowledgement=false):Promise<void> {
  if((updateApplying||updateShutdownInFlight)&&!quitComplete){show();return;}
  if(administration?.active&&!administrativeAcknowledgement){show();return;}
  if(stopExit.active){stopExit.cancel();updateTray();return;}
  if(quitting||quitComplete)return;quitting=true;
  controller?.setConnection('closing');
  let timer:ReturnType<typeof setTimeout>|undefined;
  const orderly=()=>orderlyShutdown({
    drain:async()=>{await controller?.drain();},backendPresent:()=>backend!==null,backendExited:()=>backend?.exited??true,
    persistPlacement:async()=>{if(!administrativeAcknowledgement)await controller?.savePlacement();},shutdownBackend:async()=>{if(backend)await backend.shutdown();},
    reportPlacementFailure:error=>controller?.setConnection('closing',safeError(error)),
  });
  try{
    await Promise.race([orderly(),new Promise<never>((_resolve,reject)=>{timer=setTimeout(()=>reject(new Error(t.shutdownDeadline(backend?.pid??null))),SHUTDOWN_DEADLINE_MS);})]);
    tray?.destroy();quitComplete=true;app.quit();
  }catch(error){quitting=false;controller?.setConnection('failed',safeError(error));if(!quietStartupTransfer){show();dialog.showErrorBox(t.shutdownUnconfirmed,safeError(error));}}
  finally{clearTimeout(timer);}
}
function registerIpc():void {
  if(!guard||!controller)throw new Error('IPC setup unavailable');
  ipcMain.handle('manager:connect',event=>guard!.connect(event));
  const none=shape({});
  const bind=(name:string,validate:(v:unknown)=>boolean,handler:(body:any)=>unknown)=>{
    ipcMain.handle(`manager:${name}`,async(event,envelope:unknown)=>{
      const {lease,body}=guard!.validate(event,envelope);requireValid(body,validate,`${name} request`);
      if(quitting&&name!=='getState'&&name!=='getTheme')throw new Error(t.closing);
      if(updateApplying&&!['getState','getTheme','getUpdateState'].includes(name))throw new Error('The update installation is being prepared.');
      if(adoptionPending&&!['getState','getTheme','getActivity'].includes(name))throw new Error(t.administrationPending);
      if(controller!.state.administrationPending&&!['getState','getTheme','getActivity','requestAdministration'].includes(name))throw new Error(t.administrationPending);
      const value=await documentScope.run(()=>guard!.current(lease),()=>handler(body));
      if(!guard!.current(lease))throw new Error(t.documentChanged);
      return value;
    });
  };
  bind('getState',none,()=>controller!.state);bind('getTheme',none,theme);
  bind('getUpdateState',none,()=>updates!.snapshot);
  bind('checkUpdates',none,()=>updates!.check());
  bind('installUpdate',shape({id:text(100)}),v=>updates!.install(v.id));
  bind('setUpdateChannel',shape({channel:oneOf('stable','test')}),v=>updates!.setChannel(v.channel));
  bind('requestAdministration',none,()=>requestAdministration());
  bind('refresh',none,()=>controller!.refresh());bind('addFiles',none,()=>controller!.add('scripts'));bind('addFolder',none,()=>controller!.add('folder'));
  bind('removeScript',shape({scriptId:guid,revision:decimal}),v=>controller!.remove(v.scriptId,v.revision));
  bind('locateScript',shape({selection}),v=>controller!.locate(assertSelection(v.selection)));
  bind('deleteScriptFile',shape({selection}),v=>controller!.deleteFile(assertSelection(v.selection)));
  bind('saveMetadata',shape({scriptId:guid,metadata,revision:decimal}),v=>controller!.saveMetadata(v.scriptId,assertMetadata(v.metadata),v.revision));
  bind('saveCollection',shape({collectionId:nullable(guid),name:text(4096)}),v=>controller!.saveCollection(v.collectionId,v.name));
  bind('removeCollection',shape({collectionId:guid,revision:decimal}),v=>controller!.removeCollection(v.collectionId,v.revision));
  bind('operate',shape({operation,selection}),v=>controller!.operate(assertOperation(v.operation),assertSelection(v.selection)));
  bind('saveSettings',()=>true,v=>controller!.saveSettings(assertSettings(v)));
  bind('pickInterpreter',none,()=>controller!.pick('interpreter'));bind('pickEditor',none,()=>controller!.pick('editor'));
  bind('getActivity',activityQuery,v=>controller!.activity(assertActivityQuery(v)));
  bind('previewImport',none,()=>controller!.previewImport());bind('commitImport',shape({token:guid}),v=>controller!.commitImport(v.token));
  bind('migrateProfile',none,()=>controller!.migrateProfile());bind('transferStartup',none,()=>controller!.transferStartup());
  bind('restoreBackup',none,()=>controller!.restoreBackup());bind('recoverPending',none,()=>controller!.recoverPending());
  bind('retryBackend',none,async()=>{if(updateShutdownInFlight)throw new Error('Wait for confirmed backend shutdown.');await startBackend();if(backend?.ready)await runLogin();});
  bind('copyText',shape({text:text(65536)}),v=>clipboard.writeText(v.text));
  ipcMain.on('manager:updateSelection',(event,envelope:unknown)=>{try{const {body}=guard!.validate(event,envelope);const selected=body===null?null:assertSelection(body);controller!.updateSelection(selected);diagnosticSelection=selected?.scriptId??null;diagnostics?.selectionCommitted(diagnosticSelection);}catch{/* Untrusted fire-and-forget messages cannot alter host authority. */}});
  ipcMain.on('manager:documentReady',(event,envelope:unknown)=>{try{const {lease,body}=guard!.validate(event,envelope);requireValid(body,none,'document ready');documentLease=lease;rendererReady=true;deliverPreferences();}catch{/* No authority for invalid readiness signals. */}});
  ipcMain.on('manager:administrationProvider',(event,envelope:unknown)=>{try{const {body}=guard!.validate(event,envelope);const value=requireValid<{registered:boolean}>(body,shape({registered:bool}),'administration provider');preflight.register(value.registered);}catch{/* Unauthenticated registration cannot enable preflight. */}});
  ipcMain.on('manager:administrationPreflightReply',(event,envelope:unknown)=>{try{const {body}=guard!.validate(event,envelope);const value=requireValid<{request:{operationId:string;phase:'begin'|'commit';nonce:string};value:AdministrationUiGuard|null}>(body,shape({request:shape({operationId:guid,phase:oneOf('begin','commit'),nonce:guid}),value:nullable(shape({dirtyDrafts:bool,dialogOpen:bool,pendingWork:bool}))}),'administration preflight');preflight.reply(value.request,value.value);}catch{/* Malformed or obsolete replies cannot authorize Begin or Commit. */}});
}
