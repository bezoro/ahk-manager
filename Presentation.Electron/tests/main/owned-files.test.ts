import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
const {validateRelative,collectOwnedFiles,renderOwnedFiles}=require('../../scripts/owned-files.cjs') as {
  validateRelative(value:string):string;
  collectOwnedFiles(root:string):Array<{relative:string;sha512:string;bytes:number}>;
  renderOwnedFiles(rows:Array<{relative:string;sha512:string;bytes:number}>):string;
};
test('owned-file path validation rejects escaping, NSIS interpolation and device names',()=>{
  for(const name of ['../file','a/../../b','C:/file','/file','a//b','a/$INSTDIR','a/"file','NUL','a/CON.txt','a/file.','a/file '])assert.throws(()=>validateRelative(name));
  assert.equal(validateRelative('backend\\AhkManager.Backend.exe'),'backend/AhkManager.Backend.exe');
});
test('immutable packaged allowlist preserves later foreign files and guards all targets before exact deletion',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-owned-list-'));
  try{
    fs.mkdirSync(path.join(root,'backend'));fs.writeFileSync(path.join(root,'app.exe'),'owned');fs.writeFileSync(path.join(root,'backend/core.dll'),'runtime');
    const rows=collectOwnedFiles(root);fs.writeFileSync(path.join(root,'user-script.ahk'),'user file');const nsis=renderOwnedFiles(rows);
    assert.equal(rows.some(row=>row.relative==='user-script.ahk'),false);assert.doesNotMatch(nsis,/user-script|RMDir \/r|Delete .*\*/);
    assert.match(nsis,/!macro customRemoveFiles/);assert.match(nsis,/!insertmacro validateOwnedRemoval/);assert.match(nsis,/0x400/);assert.match(nsis,/SHA2-512/);
    assert.ok(nsis.indexOf('!macro validateOwnedRemoval')<nsis.indexOf('Delete "$INSTDIR\\app.exe"'));
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('redirected package directories cannot create an ownership allowlist',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-owned-link-'));const outside=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-owned-outside-'));
  try{fs.symlinkSync(outside,path.join(root,'redirect'),process.platform==='win32'?'junction':'dir');assert.throws(()=>collectOwnedFiles(root),/redirected/);}
  finally{fs.rmSync(root,{recursive:true,force:true});fs.rmSync(outside,{recursive:true,force:true});}
});
test('junction above an otherwise normal installation root rejects ownership before any file effect',()=>{
  const container=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-owned-parent-'));const outside=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-owned-parent-target-'));
  try{
    fs.mkdirSync(path.join(outside,'install'));fs.writeFileSync(path.join(outside,'install/app.exe'),'owned bytes copied elsewhere');
    fs.symlinkSync(outside,path.join(container,'ancestor'),process.platform==='win32'?'junction':'dir');
    const installRoot=path.join(container,'ancestor','install');assert.equal(fs.lstatSync(installRoot).isDirectory(),true);assert.equal(fs.lstatSync(installRoot).isSymbolicLink(),false);
    assert.throws(()=>collectOwnedFiles(installRoot),/ancestor is redirected/);assert.equal(fs.readFileSync(path.join(outside,'install/app.exe'),'utf8'),'owned bytes copied elsewhere');
  }finally{fs.rmSync(container,{recursive:true,force:true});fs.rmSync(outside,{recursive:true,force:true});}
});
test('updated invocation is rejected in initialization and before startup/file effect macros',()=>{
  const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
  const macro=(name:string)=>installer.slice(installer.indexOf('!macro '+name),installer.indexOf('!macroend',installer.indexOf('!macro '+name)));
  assert.match(macro('rejectInPlaceUpdate'),/\$\{If\} \$\{isUpdated\}[\s\S]*!insertmacro abortManualUpdate/);
  assert.match(macro('abortManualUpdate'),/SetErrorLevel 2[\s\S]*Abort/);
  assert.match(macro('preInit'),/rejectInPlaceUpdate[\s\S]*rejectRegisteredInstallation HKCU[\s\S]*rejectRegisteredInstallation HKLM/);
  assert.match(macro('customUnInit'),/rejectInPlaceUpdate/);
  const uninstall=macro('customUnInstall');assert.ok(uninstall.indexOf('rejectInPlaceUpdate')<uninstall.indexOf('validateOwnedRemoval'));assert.ok(uninstall.indexOf('rejectInPlaceUpdate')<uninstall.indexOf('DeleteRegValue'));
  const generated=renderOwnedFiles([{relative:'app.exe',sha512:'a'.repeat(128),bytes:1}]);const removal=generated.slice(generated.indexOf('!macro customRemoveFiles'));
  assert.ok(removal.indexOf('rejectInPlaceUpdate')<removal.indexOf('validateOwnedRemoval'));assert.ok(removal.indexOf('rejectInPlaceUpdate')<removal.indexOf('Delete '));
});

test('uninstall checks owned administrative startup before registration or file removal without a policy bypass',()=>{
  const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
  const uninstall=installer.slice(installer.indexOf('!macro customUnInstall'),installer.indexOf('!macroend',installer.indexOf('!macro customUnInstall')));
  assert.ok(uninstall.indexOf('validateOwnedRemoval')<uninstall.indexOf('checkOwnedAdministrativeStartup'));
  assert.ok(uninstall.indexOf('checkOwnedAdministrativeStartup')<uninstall.indexOf('DeleteRegValue'));
  const gate=installer.slice(installer.indexOf('!macro checkOwnedAdministrativeStartup'),installer.indexOf('!macroend',installer.indexOf('!macro checkOwnedAdministrativeStartup')));
  assert.match(gate,/-File "\$PLUGINSDIR\\Check-StartupTask\.ps1" -ManagerExecutable "\$INSTDIR\\Bezoro\.AhkManager\.exe"/);
  assert.match(gate,/\$R2 == 2[\s\S]*Abort[\s\S]*\$R2 != 0[\s\S]*Abort/);
  const helper=fs.readFileSync(path.resolve('scripts/Check-StartupTask.ps1'),'utf8');
  assert.match(helper,/GetTask\('Bezoro\.AhkManager\.Electron\.'/);
  assert.match(helper,/Bezoro\.AhkManager\.Electron\.Startup\.v1/);
  assert.match(helper,/OrdinalIgnoreCase/);
  assert.match(helper,/InteractiveToken/);
  assert.doesNotMatch(gate+helper,/-ExecutionPolicy\s+Bypass|RegisterTask|DeleteTask|Remove-ScheduledTask|Start-Process|Invoke-Expression/);
});

test('uninstall inspector recognizes only exact owned task XML without accessing COM', {skip:process.platform!=='win32'},()=>{
  const script=String.raw`
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($env:AHK_TEST_STARTUP_INSPECTOR,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Inspector syntax failed'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-OwnedStartupTask'},$true)
if($null -eq $definition){throw 'Inspector function missing'}
# Load just the pure XML function; the script's COM/identity body is not executed.
. ([ScriptBlock]::Create($definition.Extent.Text))
$sid='S-1-5-21-fixture'
$expected=[IO.Path]::GetFullPath('C:\Program Files\AHK Manager\Bezoro.AhkManager.exe')
$xml='<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Source>Bezoro.AhkManager.Electron.Startup.v1</Source></RegistrationInfo><Principals><Principal><UserId>S-1-5-21-fixture</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals><Triggers><LogonTrigger><UserId>S-1-5-21-fixture</UserId></LogonTrigger></Triggers><Actions><Exec><Command>C:\Program Files\AHK Manager\Bezoro.AhkManager.exe</Command><Arguments>--login --background</Arguments></Exec></Actions></Task>'
if(-not(Test-OwnedStartupTask $xml $expected $sid)){throw 'Owned task not recognized'}
foreach($foreign in @($xml.Replace('Startup.v1','Startup.foreign'),$xml.Replace('Bezoro.AhkManager.exe','another.exe'),$xml.Replace('S-1-5-21-fixture','S-1-5-21-other'),$xml.Replace('</Actions>','<Exec/></Actions>'),$xml.Replace('</Triggers>','<BootTrigger/></Triggers>'),$xml.Replace('InteractiveToken','Password'),$xml.Replace('--login --background','--login --background --extra'))){if(Test-OwnedStartupTask $foreign $expected $sid){throw 'Foreign task recognized as owned'}}
$rejected=$false
try{Test-OwnedStartupTask '<!DOCTYPE Task [<!ENTITY a "expanded">]><Task>&a;</Task>' $expected $sid | Out-Null}catch{$rejected=$true}
if(-not$rejected){throw 'DTD not refused'}
'Ownership XML cases passed; no COM access'
`;
  const result=spawnSync('powershell.exe',['-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{shell:false,windowsHide:true,timeout:15000,encoding:'utf8',env:{...process.env,AHK_TEST_STARTUP_INSPECTOR:path.resolve('scripts/Check-StartupTask.ps1')}});
  assert.ifError(result.error);assert.equal(result.status,0,result.stdout+'\n'+result.stderr);
});
