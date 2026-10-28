import test from 'node:test';
import assert from 'node:assert/strict';
import { APP_URL, assetFromUrl, CSP, DocumentGuard, packagedBackend } from '../../src/main/security.js';
import { parseStrictJson, snapshot as validatesSnapshot, decimal } from '../../src/shared/validation.js';
import { snapshot } from './fixtures.js';
import { capturePlacement, restorePlacement } from '../../src/main/placement.js';

test('fixed app protocol allows only exact packaged assets and rejects network, traversal and query routes',()=>{
  const allow=new Set(['index.html','app.js','app.css']);
  assert.equal(assetFromUrl(APP_URL,allow),'index.html');
  for(const url of ['https://app/index.html','file:///index.html','ahk-manager://other/index.html','ahk-manager://app/%2e%2e/secrets','ahk-manager://app/%2f../index.html','ahk-manager://app/app.js?remote=1','ahk-manager://app/app.js#x','ahk-manager://user@app/index.html','ahk-manager://app/package.json','ahk-manager://app/%5cindex.html'])assert.equal(assetFromUrl(url,allow),null,url);
  assert.match(CSP,/script-src 'self';/);assert.doesNotMatch(CSP,/unsafe-eval/);assert.match(CSP,/connect-src 'none'/);assert.match(CSP,/frame-src 'none'/);
  assert.equal(packagedBackend('C:\\App'),'C:\\App\\backend\\AhkManager.Backend.exe');
  assert.throws(()=>packagedBackend('relative'));
});
test('document guard rejects subframes, origins, stale generation and stale async state sends',()=>{
  const sent:unknown[]=[];const frame={url:APP_URL,processId:10,routingId:20,isDestroyed:()=>false,send:(...args:unknown[])=>sent.push(args)};
  const contents={mainFrame:frame,isDestroyed:()=>false};const guard=new DocumentGuard(contents as any);
  const event={sender:contents,senderFrame:frame} as any;
  const lease=guard.connect(event);assert.equal(guard.current(lease),true);
  assert.throws(()=>guard.validate({...event,senderFrame:{...frame}} as any,{token:lease.token,body:{}}));
  assert.throws(()=>guard.validate(event,{token:'forged',body:{}}));
  guard.send('state',{});assert.equal(sent.length,1);
  guard.invalidate();assert.equal(guard.current(lease),false);guard.send('state',{});assert.equal(sent.length,1);
  assert.throws(()=>guard.validate(event,{token:lease.token,body:{}}));
  frame.url='https://example.com';assert.throws(()=>guard.connect(event));
});
test('wire validation retains Int64 without precision loss, rejects forged nested data and duplicate escaped keys',()=>{
  const state=snapshot();assert.equal(validatesSnapshot(state),true);assert.equal(decimal(state.revision),true);
  assert.equal(decimal('9223372036854775808'),false);assert.equal(decimal('01'),false);assert.equal(decimal(9007199254740992),false);
  assert.equal(validatesSnapshot({...state,revision:9223372036854775807n}),false);
  assert.equal(validatesSnapshot({...state,settings:{...state.settings,startupConsent:true}}),false);
  assert.equal(validatesSnapshot({...state,settings:{...state.settings,startInTray:true}}),true);
  assert.equal(validatesSnapshot({...state,settings:{...state.settings,startInTray:'true'}}),false);
  assert.equal(validatesSnapshot({...state,scripts:[{...state.scripts[0],status:'Invented'}]}),false);
  for(const json of ['{"id":1,"id":2}','{"id":1,"\\u0069d":2}','{"a":{"b":1,"b":2}}'])assert.throws(()=>parseStrictJson(json));
  assert.deepEqual(parseStrictJson('{"unicode":"ação 😀","a":[{"x":true}]}'),{unicode:'ação 😀',a:[{x:true}]});
});
test('placement restores legacy physical coordinates in DIP and clamps missing displays',()=>{
  const monitor={workArea:{x:0,y:0,width:1920,height:1080},scaleFactor:1.5};
  assert.deepEqual(restorePlacement({x:150,y:150,width:1800,height:1170,dpi:144,maximized:false},[monitor]),{x:100,y:100,width:1200,height:780});
  const missing=restorePlacement({x:30000,y:-30000,width:1200,height:780,dpi:96,maximized:false},[monitor]);assert.equal(missing.x,720);assert.equal(missing.y,0);
  assert.deepEqual(capturePlacement({x:100,y:100,width:1200,height:780},1.5,false),{x:150,y:150,width:1800,height:1170,dpi:144,maximized:false});
});
