import type { ManagerSnapshot, SelectionContext } from '../../src/shared/contracts.js';
export const scriptId='11111111-1111-1111-1111-111111111111';
export const planId='22222222-2222-2222-2222-222222222222';
export function snapshot():ManagerSnapshot {return {
  revision:'9223372036854775807',scripts:[{id:scriptId,path:'C:\\fixtures\\teste.ahk',friendlyName:'Teste',description:'Descrição',collectionId:null,runAtLogin:false,runAsAdministrator:false,launchBlocked:false,status:'Stopped',process:null,hotkeys:[],coverage:'Partial',issues:[]}],collections:[],
  settings:{interpreterPath:null,editorPath:null,editorArguments:[],launchManagerAtLogin:false,showWindowAtLogin:false,theme:'System',closeBehavior:'HideToTray',window:{x:100,y:100,width:1200,height:780,maximized:false,dpi:96}},activity:[],issues:[],readOnly:false,effectsAvailable:true,observedAt:null,runningCount:0,problemCount:0,
};}
export function selection():SelectionContext {return {scriptId,stateRevision:snapshot().revision,query:{search:'',status:'All',sort:'Name',collectionId:null,version:'9007199254740993'}};}

/** Backend observations only; these identities never address real processes. */
export function runningSnapshot(count=1):ManagerSnapshot {
  const state=snapshot();state.observedAt='2026-10-23T00:00:00Z';state.revision='1';
  state.scripts=Array.from({length:count},(_,i)=>{
    const row={...state.scripts[0]!,id:`11111111-1111-1111-1111-${String(i+1).padStart(12,'0')}`,path:`C:\\fixtures\\script-${i}.ahk`,friendlyName:`Script ${i}`,status:'Running' as const};
    return {...row,process:{identity:{pid:100+i,creationFileTime:String(9007199254740993n+BigInt(i)),executablePath:'C:\\fixtures\\AutoHotkey64.exe',scriptPath:row.path,userSid:'S-1-5-21-fixture',sessionId:7,scriptHash:'a'.repeat(64),interpreterHash:'b'.repeat(64)},origin:'External' as const,interpreterVersion:'2.0.19',startedAt:state.observedAt!,paused:false,suspended:false,windowVerified:true}};
  });state.runningCount=count;return state;
}
