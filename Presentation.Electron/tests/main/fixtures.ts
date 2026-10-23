import type { ManagerSnapshot, SelectionContext } from '../../src/shared/contracts.js';
export const scriptId='11111111-1111-1111-1111-111111111111';
export const planId='22222222-2222-2222-2222-222222222222';
export function snapshot():ManagerSnapshot {return {
  revision:'9223372036854775807',scripts:[{id:scriptId,path:'C:\\fixtures\\teste.ahk',friendlyName:'Teste',description:'Descrição',collectionId:null,runAtLogin:false,runAsAdministrator:false,launchBlocked:false,status:'Stopped',process:null,hotkeys:[],coverage:'Partial',issues:[]}],collections:[],
  settings:{interpreterPath:null,editorPath:null,editorArguments:[],launchManagerAtLogin:false,showWindowAtLogin:false,theme:'System',closeBehavior:'HideToTray',window:{x:100,y:100,width:1200,height:780,maximized:false,dpi:96}},activity:[],issues:[],readOnly:false,effectsAvailable:true,observedAt:null,runningCount:0,problemCount:0,
};}
export function selection():SelectionContext {return {scriptId,stateRevision:snapshot().revision,query:{search:'',status:'All',sort:'Name',collectionId:null,version:'9007199254740993'}};}
