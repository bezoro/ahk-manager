import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { scriptFileStamp } from '../../src/main/script-files.js';
import { snapshot as validSnapshot, hotkey } from '../../src/shared/validation.js';
import { snapshot } from './fixtures.js';

test('host stamp refuses changed hash, multiply linked file, directory, UNC and junction ancestors',{skip:process.platform!=='win32'?'Requires Windows drive paths and junction semantics; covered by Windows CI.':false},async()=>{
  const root=await fs.mkdtemp(path.join(process.env.AHK_DIAGNOSTICS_TEST_ROOT!,'script-files-'));
  const target=path.join(root,'inert.ahk');const body='F13::return\n';await fs.writeFile(target,body);
  const file={path:target,fileId:'backend-verified',hash:createHash('sha256').update(body).digest('hex')};
  assert.equal(await scriptFileStamp(file),await scriptFileStamp(file));
  await assert.rejects(scriptFileStamp({...file,hash:'0'.repeat(64)}));
  const linked=path.join(root,'linked.ahk');await fs.link(target,linked);await assert.rejects(scriptFileStamp(file));
  const folder=path.join(root,'directory.ahk');await fs.mkdir(folder);await assert.rejects(scriptFileStamp({...file,path:folder}));
  await assert.rejects(scriptFileStamp({...file,path:'\\\\host\\share\\inert.ahk'}));
  const source=path.join(root,'actual');await fs.mkdir(source);await fs.writeFile(path.join(source,'inert.ahk'),body);
  const junction=path.join(root,'redirected');await fs.symlink(source,junction,'junction');await assert.rejects(scriptFileStamp({...file,path:path.join(junction,'inert.ahk')}));
});
test('active transport strictly allows absent/null/DoubleClick and rejects wrong gesture shape',()=>{
  const key={chord:'~LButton Up',normalizedChord:'LBUTTON UP',origin:'Parsed',context:null,sourcePath:null,line:1,isRemap:false,possibleConflict:false};
  for(const value of [key,{...key,declaredGesture:null},{...key,declaredGesture:'DoubleClick'}]){assert.ok(hotkey(value));const state=snapshot();state.scripts[0]!.hotkeys=[value as never];assert.ok(validSnapshot(state));}
  for(const value of ['double-click','Unknown',12,{},[],true])assert.equal(hotkey({...key,declaredGesture:value}),false);
  assert.equal(hotkey({...key,arbitraryGesture:'DoubleClick'}),false);
});
