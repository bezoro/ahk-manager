// Explicit fixture host: no backend, real profile, startup writes or script operations.
import { app,BrowserWindow,ipcMain,nativeTheme,protocol,systemPreferences } from 'electron';
import fs from 'node:fs';
import path from 'node:path';
import { APP_URL,assetFromUrl,CSP } from '../../src/main/security.js';
import type { HostState,ManagerSnapshot,ThemeState,OperationResult } from '../../src/shared/contracts.js';
import { snapshot,scriptId } from './fixtures.js';
import { requireValid,shape,snapshot as validateSnapshot,nullable,text,bool,oneOf } from '../../src/shared/validation.js';
const arg=(name:string)=>{const index=process.argv.indexOf(name);if(index<0)throw new Error(`Missing ${name}`);return process.argv[index+1]!;};
const output=path.resolve(arg('--output')),frontend=path.resolve(arg('--frontend'));
const scenariosIndex=process.argv.indexOf('--scenarios');const scenarios=scenariosIndex>=0?fs.readFileSync(path.resolve(process.argv[scenariosIndex+1]!),'utf8'):null;
if(fs.existsSync(output))throw new Error('Fresh isolated fixture output required');fs.mkdirSync(output,{recursive:true});
app.setPath('userData',path.join(output,'Chromium'));app.setPath('sessionData',path.join(output,'session'));app.setPath('crashDumps',path.join(output,'crashes'));
protocol.registerSchemesAsPrivileged([{scheme:'ahk-manager',privileges:{standard:true,secure:true,supportFetchAPI:true}}]);
let window:BrowserWindow;let material:ThemeState['material']='solid';
let calls:{method:string;args:unknown[]}[]=[];
const scripts=snapshot().scripts;
const example=scripts[0]!;
const fixtureSnapshot:ManagerSnapshot={...snapshot(),revision:'1',collections:[{id:'33333333-3333-3333-3333-333333333333',name:'Produtividade'},{id:'44444444-4444-4444-4444-444444444444',name:'Sistema'}],scripts:[
  {...example,friendlyName:'Captura de tela',description:'Capturas e anotações rápidas — dados de teste',path:'C:\\FixtureIsolada\\Captura.ahk',collectionId:'33333333-3333-3333-3333-333333333333',runAtLogin:true,status:'Running',hotkeys:[{chord:'# + S',normalizedChord:'#+S',origin:'Parsed',context:null,sourcePath:null,line:3,isRemap:false,possibleConflict:false}],process:{identity:{pid:12000,creationFileTime:'134000000000000001',executablePath:'C:\\FixtureIsolada\\AutoHotkey64.exe',scriptPath:'C:\\FixtureIsolada\\Captura.ahk',userSid:'S-1-5-21-fixture',sessionId:1,scriptHash:'',interpreterHash:''},origin:'Manager',interpreterVersion:'2.0.19',startedAt:new Date(Date.now()-480000).toISOString(),paused:null,suspended:false,windowVerified:true}},
  {...example,id:'55555555-5555-5555-5555-555555555555',friendlyName:'Expansão de texto',path:'C:\\FixtureIsolada\\Texto.ahk',collectionId:'33333333-3333-3333-3333-333333333333'},
  {...example,id:'66666666-6666-6666-6666-666666666666',friendlyName:'Organizar janelas',path:'C:\\FixtureIsolada\\Janelas.ahk',collectionId:'44444444-4444-4444-4444-444444444444',status:'Unknown',issues:[{code:'ObservationIncomplete',detail:'A identidade não foi confirmada nesta fixture.',scriptId:null}]},
  {...example,id:'77777777-7777-7777-7777-777777777777',friendlyName:'Modo de concentração',path:'C:\\FixtureIsolada\\Foco.ahk',hotkeys:[{chord:'Ctrl + Alt + F',normalizedChord:'^!F',origin:'Manual',context:null,sourcePath:null,line:null,isRemap:false,possibleConflict:true}]},
],issues:[{code:'PartialAnalysis',detail:'FIXTURE ISOLADA — dados de teste; nenhum script real.',scriptId:null}],activity:[{id:'88888888-8888-8888-8888-888888888888',timestamp:new Date().toISOString(),scriptId,operation:'Start',outcome:'Confirmed',code:'None',detail:'Fixture de apresentação; nenhum processo foi iniciado.'}],runningCount:1,problemCount:2};
let state:HostState={snapshot:fixtureSnapshot,connection:'ready',error:null,busy:false};
const currentTheme=():ThemeState=>({dark:nativeTheme.shouldUseDarkColors,accent:`#${systemPreferences.getAccentColor().slice(0,6)}`,highContrast:nativeTheme.shouldUseHighContrastColors,reducedMotion:!systemPreferences.getAnimationSettings().shouldRenderRichAnimation,material});
const write=(name:string,value:unknown)=>{const file=path.join(output,name);fs.writeFileSync(file+'.tmp',JSON.stringify(value,null,2));fs.renameSync(file+'.tmp',file);};
function report(){write('state.json',{pid:process.pid,hwnd:window.getNativeWindowHandle().readBigUInt64LE().toString(),bounds:window.getBounds(),contentBounds:window.getContentBounds(),visible:window.isVisible(),focused:window.isFocused(),fixture:true,theme:currentTheme(),versions:process.versions,backend:null});}
function emitState(){window.webContents.send('fixture:state',state);report();}
function emitTheme(){const theme=currentTheme();window.setTitleBarOverlay({height:44,color:material==='mica'?'#00000000':theme.dark?'#202020':'#f7f7f7',symbolColor:theme.dark?'#fff':'#111'});window.webContents.send('fixture:theme',theme);report();}
const result:OperationResult={outcome:'Confirmed',code:'None',detail:'Fixture isolada; nenhum efeito real.',correlationId:null,succeeded:true};
app.whenReady().then(async()=>{
  const assets=new Set(JSON.parse(fs.readFileSync(path.join(frontend,'assets.json'),'utf8')) as string[]);
  protocol.handle('ahk-manager',request=>{const name=assetFromUrl(request.url,assets);if(!name)return new Response('Forbidden',{status:403});const ext=path.extname(name);return new Response(fs.readFileSync(path.join(frontend,'renderer',name)),{headers:{'Content-Type':ext==='.html'?'text/html':ext==='.css'?'text/css':'text/javascript','Content-Security-Policy':CSP}});});
  window=new BrowserWindow({width:1240,height:820,minWidth:760,minHeight:560,show:false,title:'AHK Manager — FIXTURE ISOLADA',titleBarStyle:'hidden',titleBarOverlay:{height:44},backgroundColor:'#00000000',webPreferences:{preload:path.join(__dirname,'fixture-preload.cjs'),nodeIntegration:false,contextIsolation:true,sandbox:true,webSecurity:true,devTools:false}});
  if(Number(process.getSystemVersion().split('.')[2]??0)>=22621){window.setBackgroundMaterial('mica');material='mica';}
  window.webContents.setWindowOpenHandler(()=>({action:'deny'}));window.webContents.on('will-navigate',event=>event.preventDefault());window.webContents.session.setPermissionRequestHandler((_contents,_permission,callback)=>callback(false));
  ipcMain.handle('fixture:invoke',(event,envelope:{method:string;args:unknown[]})=>{
    if(event.sender!==window.webContents||event.senderFrame!==window.webContents.mainFrame||event.senderFrame.url!==APP_URL)throw new Error('Invalid fixture sender');
    const {method,args}=envelope;
    if(method==='calls')return calls;
    if(method==='clearCalls'){calls=[];return;}
    if(method==='setState'){requireValid(args[0],shape({snapshot:nullable(validateSnapshot),connection:oneOf('starting','ready','failed','closing'),error:nullable(text()),busy:bool}),'fixture state');state=structuredClone(args[0]) as HostState;emitState();return;}
    if(method==='setTheme'){const value=args[0] as ThemeState;window.webContents.send('fixture:theme',value);return;}
    calls.push({method,args});
    if(method==='getState')return state;if(method==='getTheme')return currentTheme();
    if(method==='refresh'||method==='retryBackend'){emitState();return;}
    if(method==='updateSelection'||method==='copyText')return;
    if(method==='getActivity')return state.snapshot?.activity??[];
    if(method==='pickInterpreter')return 'C:\\FixtureIsolada\\AutoHotkey64.exe';if(method==='pickEditor')return 'C:\\FixtureIsolada\\Editor.exe';
    if(method==='addFiles'||method==='addFolder')return {result,added:[],duplicateCount:0,issues:[]};
    if(method==='previewImport')return null;
    if(['removeScript','saveMetadata','saveCollection','removeCollection','operate','saveSettings','commitImport','migrateProfile','transferStartup','restoreBackup','recoverPending'].includes(method))return result;
    throw new Error('Unknown fixture method');
  });
  nativeTheme.on('updated',emitTheme);
  await window.loadURL(APP_URL);window.show();window.focus();emitTheme();report();
  write('ready.json',{fixture:true,backend:null,versions:process.versions,gpu:app.getGPUFeatureStatus(),sandbox:true,nodeIntegration:false});
  const seen=new Set<number>();let tail=Promise.resolve();
  fs.watch(output,(_event,name)=>{if(name!=='command.json')return;try{const command=JSON.parse(fs.readFileSync(path.join(output,name),'utf8'));if(!Number.isSafeInteger(command.id)||seen.has(command.id))return;seen.add(command.id);tail=tail.then(async()=>{
    if(command.action==='capture'){
      if(!/^[a-z0-9-]+$/.test(command.name))throw new Error('Invalid capture name');
      fs.writeFileSync(path.join(output,command.name+'.internal.png'),(await window.webContents.capturePage()).toPNG());
      const dom=await window.webContents.executeJavaScript('({width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,active:document.activeElement?.id,fixture:!!document.getElementById("fixture-warning")})');report();write(command.name+'.done.json',{dom,theme:currentTheme(),fixture:true});
    }else if(command.action==='theme'&&['system','light','dark'].includes(command.value)){nativeTheme.themeSource=command.value;emitTheme();}
    else if(command.action==='resize'&&[760,1000,1240,1440].includes(command.width)&&[600,720,820,900].includes(command.height)){window.setSize(command.width,command.height);report();}
    else if(command.action==='assertions'&&scenarios){const assertions=await window.webContents.executeJavaScript(scenarios+'\nAhkRendererScenarios.runScenarios()');write('assertions-'+command.id+'.json',{assertions,fixture:true});}
    else if(command.action==='preferences'){window.webContents.send('fixture:preferences');report();}
    else if(command.action==='close'){nativeTheme.themeSource='system';write('closed.json',{themeRestored:nativeTheme.themeSource,fixture:true});app.quit();}
    else throw new Error('Unknown fixture command');
    write('command-'+command.id+'.done.json',{action:command.action});
  }).catch(error=>write('command-'+command.id+'.error.json',{error:String(error)}));}catch(error){write('watch-error.json',{error:String(error)});}});
  window.on('resize',report);window.on('focus',report);
}).catch(error=>{write('startup-error.json',{error:String(error)});app.quit();});
app.on('window-all-closed',()=>app.quit());
