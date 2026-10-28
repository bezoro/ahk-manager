import { contextBridge, ipcRenderer } from 'electron';
import type { AdministrationUiGuard, AhkManagerApi, HostState, ThemeState } from '../shared/contracts.js';
import { activity, array, authorityMode, bool, catalogResult, guid, importPreview, nullable, object, oneOf, operationResult, shape, snapshot, text, requireValid, type Validator } from '../shared/validation.js';
import { updateState } from '../shared/update-validation.js';

const hostState=shape({snapshot:nullable(snapshot),connection:oneOf('starting','ready','failed','closing'),error:nullable(text()),busy:bool,authorityMode:nullable(authorityMode),administrationPending:bool});
const uiGuard=shape({dirtyDrafts:bool,dialogOpen:bool,pendingWork:bool});
const preflightRequest=shape({operationId:guid,phase:oneOf('begin','commit'),nonce:guid});
let preflightProvider:(()=>AdministrationUiGuard)|null=null;
ipcRenderer.on('manager:administrationPreflight',(_event,envelope:unknown)=>{
  if(!object(envelope)||envelope.token!==token||!preflightRequest(envelope.body)||!preflightProvider)return;
  let value:AdministrationUiGuard|null=null;
  try{value=requireValid<AdministrationUiGuard>(preflightProvider(),uiGuard,'administration UI guard');}catch{/* An unavailable provider refuses authority. */}
  ipcRenderer.send('manager:administrationPreflightReply',{token,body:{request:envelope.body,value}});
});
const themeState=shape({dark:bool,highContrast:bool,reducedMotion:bool,accent:text(16),material:oneOf('mica','solid')});
let tokenPromise:Promise<string>|null=null;
let token:string|null=null;
function connect():Promise<string> {
  tokenPromise??=ipcRenderer.invoke('manager:connect').then((v:unknown)=>{
    if(!object(v)||typeof v.token!=='string'||!/^[a-f0-9]{64}$/.test(v.token))throw new Error('Invalid document handshake');
    token=v.token;ipcRenderer.send('manager:documentReady',{token,body:{}});return token;
  });
  return tokenPromise;
}
const invoke=async<T>(name:string,body:unknown={}):Promise<T>=>ipcRenderer.invoke(`manager:${name}`,{token:await connect(),body}) as Promise<T>;
const checked=async<T>(name:string,body:unknown,validate:Validator):Promise<T>=>requireValid<T>(await invoke(name,body),validate,`${name} response`);
const empty:Validator=v=>v===undefined;
function subscribe<T>(channel:string,listener:(value:T)=>void,validate:(value:unknown)=>boolean):()=>void {
  const handler=(_event:Electron.IpcRendererEvent,envelope:unknown)=>{
    if(!object(envelope)||envelope.token!==token||!validate(envelope.body))return;
    listener(envelope.body as T);
  };
  ipcRenderer.on(channel,handler);
  void connect();
  return ()=>ipcRenderer.removeListener(channel,handler);
}
const api:AhkManagerApi={
  getUpdateState:()=>checked('getUpdateState',{},updateState),
  checkUpdates:()=>checked('checkUpdates',{},updateState),
  installUpdate:id=>checked('installUpdate',{id},updateState),
  setUpdateToken:token=>checked('setUpdateToken',{token},updateState),
  onUpdates:listener=>subscribe('manager:updates',listener,updateState),
  requestAdministration:()=>checked('requestAdministration',{},operationResult),
  onAdministrationPreflight:provider=>{
    preflightProvider=provider;
    void connect().then(value=>{if(preflightProvider===provider)ipcRenderer.send('manager:administrationProvider',{token:value,body:{registered:true}});});
    return ()=>{if(preflightProvider!==provider)return;preflightProvider=null;void connect().then(value=>{if(!preflightProvider)ipcRenderer.send('manager:administrationProvider',{token:value,body:{registered:false}});});};
  },
  getState:async()=>requireValid<HostState>(await invoke('getState'),hostState,'host state'),
  onState:listener=>subscribe('manager:state',listener,hostState),
  refresh:()=>checked('refresh',{},empty),addFiles:()=>checked('addFiles',{},nullable(catalogResult)),addFolder:()=>checked('addFolder',{},nullable(catalogResult)),
  removeScript:(scriptId,revision)=>checked('removeScript',{scriptId,revision},operationResult),
  locateScript:selection=>checked('locateScript',{selection},operationResult),
  deleteScriptFile:selection=>checked('deleteScriptFile',{selection},operationResult),
  saveMetadata:(scriptId,metadata,revision)=>checked('saveMetadata',{scriptId,metadata,revision},operationResult),
  saveCollection:(collectionId,name)=>checked('saveCollection',{collectionId,name},operationResult),
  removeCollection:(collectionId,revision)=>checked('removeCollection',{collectionId,revision},operationResult),
  operate:(operation,selection)=>checked('operate',{operation,selection},operationResult),
  updateSelection:selection=>{void connect().then(value=>ipcRenderer.send('manager:updateSelection',{token:value,body:selection}));},
  saveSettings:settings=>checked('saveSettings',settings,operationResult),
  pickInterpreter:()=>checked('pickInterpreter',{},nullable(text())),pickEditor:()=>checked('pickEditor',{},nullable(text())),
  getActivity:query=>checked('getActivity',query,array(activity)),previewImport:()=>checked('previewImport',{},nullable(shape({token:guid,preview:importPreview}))),commitImport:token=>checked('commitImport',{token},operationResult),
  migrateProfile:()=>checked('migrateProfile',{},nullable(operationResult)),transferStartup:()=>checked('transferStartup',{},operationResult),restoreBackup:()=>checked('restoreBackup',{},operationResult),recoverPending:()=>checked('recoverPending',{},operationResult),
  retryBackend:()=>checked('retryBackend',{},empty),copyText:text=>checked('copyText',{text},empty),
  getTheme:async()=>requireValid<ThemeState>(await invoke('getTheme'),themeState,'theme'),
  onTheme:listener=>subscribe('manager:theme',listener,themeState),
  onOpenPreferences:listener=>{
    preferenceListeners.add(listener);if(preferencePending){preferencePending=false;queueMicrotask(listener);}void connect();
    return()=>{preferenceListeners.delete(listener);};
  },
};
let preferencePending=false;
const preferenceListeners=new Set<()=>void>();
ipcRenderer.on('manager:preferences',(_event,envelope:unknown)=>{
  if(!object(envelope)||envelope.token!==token||envelope.body!==null)return;
  if(!preferenceListeners.size)preferencePending=true;else for(const listener of preferenceListeners)listener();
});
contextBridge.exposeInMainWorld('ahkManager',Object.freeze(api));
