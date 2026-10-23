import { app, BrowserWindow, clipboard, dialog, ipcMain, Menu, nativeImage, nativeTheme, protocol, screen, session, systemPreferences, Tray } from 'electron';
import path from 'node:path';
import fs from 'node:fs/promises';
import os from 'node:os';
import { AsyncLocalStorage } from 'node:async_hooks';
import { BackendClient } from './backend.js';
import { ManagerController } from './controller.js';
import { LifecycleIntent, StopExitIntent, SHUTDOWN_DEADLINE_MS, orderlyShutdown } from './lifecycle.js';
import { createDiagnostics, type PageDiagnostics } from './diagnostics.js';
import {hostText as t} from '../shared/host-pt-BR.js';
import { APP_URL, assetFromUrl, CSP, DocumentGuard, packagedBackend, type DocumentLease } from './security.js';
import { capturePlacement, restorePlacement } from './placement.js';
import type { ManagerSnapshot, ThemeState, WindowPlacement } from '../shared/contracts.js';
import { activityQuery, array, assertActivityQuery, assertMetadata, assertOperation, assertSelection, assertSettings, decimal, guid, metadata, nullable, operation, requireValid, selection, shape, text } from '../shared/validation.js';

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
const lifecycle=new LifecycleIntent(process.argv.includes('--login'));
const stopExit=new StopExitIntent();
let stopExitProgress={done:0,total:0};
let quitting=false;
let quitComplete=false;
const documentScope=new AsyncLocalStorage<()=>boolean>();
const alive=()=>!quitting&&(documentScope.getStore()?.()??(window!==null&&!window.isDestroyed()));
const single=app.requestSingleInstanceLock();
if(!single)app.quit();
else {
  app.on('second-instance',(_event,argv)=>{
    if(argv.includes('--login')){lifecycle.requestLogin();if(ready)void runLogin();}
    else {lifecycle.requestExplicitActivation();show();}
  });
  app.on('activate',()=>{lifecycle.requestExplicitActivation();show();});
  app.on('before-quit',event=>{if(!quitComplete){event.preventDefault();void quit();}});
  app.on('window-all-closed',()=>{if(!quitting)void quit();});
  void app.whenReady().then(start).catch(error=>{dialog.showErrorBox(t.title,safeError(error));quitComplete=true;app.quit();});
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
  if(window.isMinimized())window.restore();window.show();window.focus();
  deliverPreferences();
}
function deliverPreferences():void {if(lifecycle.takePreferences(ready&&rendererReady&&!!guard?.connected))guard?.send('manager:preferences',null);}
function showPreferences():void {lifecycle.requestPreferences();show();deliverPreferences();}
function placement():WindowPlacement {
  if(!window||window.isDestroyed())return controller?.state.snapshot?.settings.window??{x:100,y:100,width:1200,height:780,maximized:false,dpi:96};
  const bounds=window.isMaximized()?window.getNormalBounds():window.getBounds();
  const physical=screen.dipToScreenRect(window,bounds);
  return {...physical,maximized:window.isMaximized(),dpi:screen.getDisplayMatching(bounds).scaleFactor*96};
}
async function confirm(title:string,detail:string):Promise<boolean> {
  if(!alive()||!window)return false;
  const response=await dialog.showMessageBox(window,{type:'question',title,message:title,detail,buttons:[t.cancel,t.confirm],defaultId:0,cancelId:0,noLink:true});
  return response.response===1&&alive();
}
async function pick(kind:'scripts'|'folder'|'interpreter'|'editor'|'import'|'migration'):Promise<string[]> {
  if(!alive()||!window)return [];
  const directory=kind==='folder'||kind==='migration';
  const response=await dialog.showOpenDialog(window,{title:t.pickerTitles[kind],properties:directory?['openDirectory','dontAddToRecent']:kind==='scripts'?['openFile','multiSelections','dontAddToRecent']:['openFile','dontAddToRecent'],filters:directory?[]:kind==='scripts'?[{name:t.fileFilters.scripts,extensions:['ahk']}]:kind==='import'?[{name:t.fileFilters.catalog,extensions:['json']}]:[{name:t.fileFilters.applications,extensions:['exe']}]});
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
async function start():Promise<void> {
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
  window.webContents.on('did-start-navigation',(_event,_url,isInPlace,isMainFrame)=>{if(isMainFrame&&!isInPlace){rendererReady=false;guard?.invalidate();controller?.updateSelection(null);}});
  window.webContents.on('render-process-gone',()=>{rendererReady=false;guard?.invalidate();controller?.updateSelection(null);});
  window.webContents.on('did-finish-load',deliverPreferences);
  window.on('close',event=>{if(quitComplete)return;event.preventDefault();void closeWindow();});
  nativeTheme.on('updated',updateTheme);
  systemPreferences.on('accent-color-changed',updateTheme);
  const native={files:pick,confirm,placement,alive};
  controller=new ManagerController({request:()=>Promise.reject(new Error('Backend starting'))},native,state=>{guard?.send('manager:state',state);updateTheme();updateTray();});
  registerIpc();
  await window.loadURL(APP_URL);
  createTray();updateTheme();
  await startBackend();
  const saved=controller.state.snapshot?.settings.window;
  if(saved){const dip=screen.screenToDipRect(window,{x:saved.x,y:saved.y,width:saved.width,height:saved.height});window.setBounds(restorePlacement({...saved,...dip,dpi:96},screen.getAllDisplays()));if(saved.maximized)window.maximize();}
  ready=true;
  await runLogin();
  if(lifecycle.shouldShowAtStartup(controller.state.snapshot?.settings.showWindowAtLogin??false,trayRegistered))show();
  if(window.isVisible())diagnostics?.initialReady();
}
async function startBackend():Promise<void> {
  if(stopExit.active)throw new Error(t.stopExitBusy);
  if(quitting)throw new Error(t.closing);
  if(!controller)throw new Error('Controller unavailable');
  if(backend&&!backend.exited)throw new Error(t.backendStillAlive(backend.pid));
  backend=new BackendClient({executable:packagedBackend(path.dirname(process.execPath)),hostExecutable:process.execPath});
  controller.setAuthority(backend);controller.setConnection('starting');
  backend.on('snapshot',(snapshot:ManagerSnapshot)=>controller?.setSnapshot(snapshot));
  backend.on('unavailable',(error:Error)=>{if(!quitting)controller?.setConnection('failed',error.message);});
  try{
    await backend.start();
    if(backend.profileRoot&&!lifecycle.loginRequested){
      const exists=await fs.access(path.join(backend.profileRoot,'settings.json')).then(()=>true,()=>false);
      if(!exists&&window){
        const choice=await dialog.showMessageBox(window,{type:'question',title:t.welcomeTitle,message:t.welcomeQuestion,detail:t.welcomeDetail,buttons:[t.newProfile,t.chooseWinUiProfile],defaultId:0,cancelId:0,noLink:true});
        if(choice.response===1)await controller.migrateProfile(true);
      }
    }
    controller.setSnapshot(await backend.request<ManagerSnapshot>('load',{}));
    controller.setConnection('ready');
  }catch(error){controller.setConnection('failed',safeError(error));show();}
}
async function runLogin():Promise<void> {if(stopExit.active||quitting||!backend?.ready||!controller||!lifecycle.takeLogin())return;await controller.runLogin().catch(error=>controller?.setConnection('failed',safeError(error)));}
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
  tray.setContextMenu(Menu.buildFromTemplate([{label:t.tray.open,click:()=>{lifecycle.requestExplicitActivation();show();}},{label:window?.isVisible()?t.tray.hide:t.tray.show,click:()=>{lifecycle.requestExplicitActivation();if(window?.isVisible())window.hide();else show();updateTray();}},{type:'separator'},{label:t.tray.running(running.length),submenu:running.length?running.map(s=>({label:s.friendlyName,enabled:false})):[{label:t.tray.noneRunning,enabled:false}]},{label:t.tray.preferences,enabled:!stopExit.active,click:showPreferences},{type:'separator'},{label:t.tray.exit,enabled:!stopExit.active&&!quitting,click:()=>void quit()},{label:t.tray.stopExit,enabled:!stopExit.active&&!quitting&&controller?.state.connection==='ready',click:()=>void stopScriptsAndQuit()},{label:t.tray.stopping(stopExitProgress.done,stopExitProgress.total),visible:stopExit.active,enabled:false},{label:t.tray.cancelStopExit,visible:stopExit.active,click:()=>{stopExit.cancel();updateTray();}}]));
}
async function stopScriptsAndQuit():Promise<void> {
  if(quitting||!controller||!stopExit.begin())return;
  stopExitProgress={done:0,total:0};updateTray();
  try{
    const receipt=await controller.stopScriptsForExit({cancelled:()=>stopExit.cancelled,progress:(done,total)=>{stopExitProgress={done,total};updateTray();}});
    if(stopExit.complete(receipt.canExit)){await quit();return;}
    updateTray();show();if(window)await dialog.showMessageBox(window,{type:'warning',title:t.stopExitPartialTitle,message:t.stopExitPartialTitle,detail:t.stopExitPartial(receipt),buttons:[t.confirm],defaultId:0,cancelId:0,noLink:true});
  }catch(error){stopExit.complete(false);updateTray();show();dialog.showErrorBox(t.stopExitPartialTitle,safeError(error));}
}
async function closeWindow():Promise<void> {
  if(stopExit.active){stopExit.cancel();updateTray();return;}
  if(quitting)return;
  try{await controller?.savePlacement();}catch(error){controller?.setConnection('failed',safeError(error));}
  if(lifecycle.closeAction(controller?.state.snapshot?.settings.closeBehavior??'Exit',trayRegistered)==='hide'){window?.hide();updateTray();}
  else await quit();
}
async function quit():Promise<void> {
  if(stopExit.active){stopExit.cancel();updateTray();return;}
  if(quitting||quitComplete)return;quitting=true;
  controller?.setConnection('closing');
  let timer:ReturnType<typeof setTimeout>|undefined;
  const orderly=()=>orderlyShutdown({
    drain:async()=>{await controller?.drain();},backendPresent:()=>backend!==null,backendExited:()=>backend?.exited??true,
    persistPlacement:async()=>{await controller?.savePlacement();},shutdownBackend:async()=>{if(backend)await backend.shutdown();},
    reportPlacementFailure:error=>controller?.setConnection('closing',safeError(error)),
  });
  try{
    await Promise.race([orderly(),new Promise<never>((_resolve,reject)=>{timer=setTimeout(()=>reject(new Error(t.shutdownDeadline(backend?.pid??null))),SHUTDOWN_DEADLINE_MS);})]);
    tray?.destroy();quitComplete=true;app.quit();
  }catch(error){quitting=false;controller?.setConnection('failed',safeError(error));show();dialog.showErrorBox(t.shutdownUnconfirmed,safeError(error));}
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
      const value=await documentScope.run(()=>guard!.current(lease),()=>handler(body));
      if(!guard!.current(lease))throw new Error(t.documentChanged);
      return value;
    });
  };
  bind('getState',none,()=>controller!.state);bind('getTheme',none,theme);
  bind('refresh',none,()=>controller!.refresh());bind('addFiles',none,()=>controller!.add('scripts'));bind('addFolder',none,()=>controller!.add('folder'));
  bind('removeScript',shape({scriptId:guid,revision:decimal}),v=>controller!.remove(v.scriptId,v.revision));
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
  bind('retryBackend',none,async()=>{await startBackend();if(backend?.ready)await runLogin();});
  bind('copyText',shape({text:text(65536)}),v=>clipboard.writeText(v.text));
  ipcMain.on('manager:updateSelection',(event,envelope:unknown)=>{try{const {body}=guard!.validate(event,envelope);const selected=body===null?null:assertSelection(body);controller!.updateSelection(selected);diagnosticSelection=selected?.scriptId??null;diagnostics?.selectionCommitted(diagnosticSelection);}catch{/* Untrusted fire-and-forget messages cannot alter host authority. */}});
  ipcMain.on('manager:documentReady',(event,envelope:unknown)=>{try{const {body}=guard!.validate(event,envelope);requireValid(body,none,'document ready');rendererReady=true;deliverPreferences();}catch{/* No authority for invalid readiness signals. */}});
}
