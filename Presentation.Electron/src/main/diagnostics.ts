import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';

export interface DiagnosticBoundary { capture():Promise<Buffer>; summary():Record<string,unknown> }
export interface DiagnosticOptions { delayMs?:number; deadlineMs?:number; maxLogBytes?:number; schedule?:(fn:()=>void,ms:number)=>()=>void }
const schedule=(fn:()=>void,ms:number):(()=>void)=>{const timer=setTimeout(fn,ms);return ()=>clearTimeout(timer);};
function bounded(value:unknown,depth=0):unknown {
  if(typeof value==='string')return value.replace(/[a-f0-9]{64}/gi,'[redacted-token]').slice(0,1024);
  if(value===null||typeof value==='boolean'||typeof value==='number')return value;
  if(depth>=3)return '[bounded]';
  if(Array.isArray(value))return value.slice(0,16).map(item=>bounded(item,depth+1));
  if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).slice(0,16).map(([key,item])=>[key.slice(0,64),key==='sha256'&&typeof item==='string'&&/^[a-f0-9]{64}$/.test(item)?item:bounded(item,depth+1)]));
  return String(value).slice(0,128);
}
export function createDiagnostics(argv:readonly string[],boundary:DiagnosticBoundary,options:DiagnosticOptions={}):PageDiagnostics|null {
  const supplied=argv.filter(arg=>arg==='--diagnostics-dir'||arg.startsWith('--diagnostics-dir='));
  if(!supplied.length)return null;
  if(supplied.length!==1||!supplied[0]?.startsWith('--diagnostics-dir='))throw new Error('Supply one --diagnostics-dir=<fresh absolute directory>');
  const directory=supplied[0].slice('--diagnostics-dir='.length);
  if(!path.isAbsolute(directory)||directory.includes('\0')||path.resolve(directory)!==directory)throw new Error('Diagnostic directory must be an absolute normalized path');
  // Refuse redirected ancestors and existing destinations; diagnostic files never overwrite anything.
  for(let parent=path.dirname(directory);;parent=path.dirname(parent)){
    const info=fs.lstatSync(parent);if(!info.isDirectory()||info.isSymbolicLink())throw new Error('Unsafe diagnostic parent');
    if(path.dirname(parent)===parent)break;
  }
  fs.mkdirSync(directory);
  return new PageDiagnostics(directory,boundary,options);
}
export class PageDiagnostics {
  private closed=false;
  private bytes=0;
  private initial=false;
  private selected=false;
  private readonly pending=new Set<()=>void>();
  private readonly schedule:(fn:()=>void,ms:number)=>()=>void;
  constructor(private readonly directory:string,private readonly boundary:DiagnosticBoundary,private readonly options:DiagnosticOptions){
    this.schedule=options.schedule??schedule;this.record('diagnostics-start',{captureLimit:2,pngByteLimit:8*1024*1024,logByteLimit:options.maxLogBytes??256*1024});
  }
  record(event:string,detail:unknown={}):void {
    if(this.closed)return;
    const line=JSON.stringify({at:new Date().toISOString(),event:event.slice(0,64),detail:bounded(detail)})+'\n';
    const size=Buffer.byteLength(line);if(this.bytes+size>(this.options.maxLogBytes??256*1024))return;
    try{fs.appendFileSync(path.join(this.directory,'events.jsonl'),line,{encoding:'utf8',flag:this.bytes?'a':'wx'});this.bytes+=size;}catch{this.close(false);}
  }
  initialReady():void {if(this.initial||this.closed)return;this.initial=true;this.request('initial-ready');}
  selectionCommitted(id:string|null):void {if(!id||this.selected||this.closed)return;this.selected=true;this.request('first-selection');}
  private summary():Record<string,unknown> {try{return this.boundary.summary();}catch{return {unavailable:true};}}
  private later(fn:()=>void,ms:number):()=>void {
    let cancel:()=>void;
    cancel=this.schedule(()=>{this.pending.delete(cancel);if(!this.closed)fn();},ms);this.pending.add(cancel);return ()=>{cancel();this.pending.delete(cancel);};
  }
  private request(stage:string):void {
    this.later(()=>{
      let settled=false;
      this.record('capture-requested',{stage,summary:this.summary()});
      const cancelDeadline=this.later(()=>{settled=true;this.record('capture-timeout',{stage});},this.options.deadlineMs??3000);
      void Promise.resolve().then(()=>this.boundary.capture()).then(png=>{
        if(settled||this.closed)return;settled=true;cancelDeadline();
        if(png.length>8*1024*1024){this.record('capture-refused',{stage,bytes:png.length});return;}
        try{fs.writeFileSync(path.join(this.directory,stage+'.png'),png,{flag:'wx'});this.record('capture-complete',{stage,bytes:png.length,sha256:createHash('sha256').update(png).digest('hex'),summary:this.summary()});}
        catch(error){this.record('capture-error',{stage,message:error instanceof Error?error.message:'Capture write failed'});}
      },error=>{if(settled||this.closed)return;settled=true;cancelDeadline();this.record('capture-error',{stage,message:error instanceof Error?error.message:'Capture failed'});});
    },this.options.delayMs??500);
  }
  close(log=true):void {if(this.closed)return;if(log)this.record('diagnostics-closed');this.closed=true;for(const cancel of this.pending)cancel();this.pending.clear();}
}
