import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
const {validateRelative,validateVersion,collectOwnedFiles,renderOwnedFiles,writeOwnedFiles}=require('../../scripts/owned-files.cjs') as {
  validateRelative(value:string):string;
  validateVersion(value:unknown):string;
  collectOwnedFiles(root:string):Array<{relative:string;sha512:string;bytes:number}>;
  renderOwnedFiles(rows:Array<{relative:string;sha512:string;bytes:number}>,version:string):string;
  writeOwnedFiles(context:{appOutDir:string;packager?:{appInfo?:{version?:string}}},output:string,uninstallerIcon:string):Promise<void>;
};
test('owned-file path validation rejects escaping, NSIS interpolation and device names',()=>{
  for(const name of ['../file','a/../../b','C:/file','/file','a//b','a/$INSTDIR','a/"file','NUL','a/CON.txt','a/file.','a/file '])assert.throws(()=>validateRelative(name));
  assert.equal(validateRelative('backend\\AhkManager.Backend.exe'),'backend/AhkManager.Backend.exe');
});
test('immutable packaged allowlist preserves later foreign files and guards all targets before exact deletion',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-owned-list-'));
  try{
    fs.mkdirSync(path.join(root,'backend'));fs.writeFileSync(path.join(root,'app.exe'),'owned');fs.writeFileSync(path.join(root,'backend/core.dll'),'runtime');
    const rows=collectOwnedFiles(root);fs.writeFileSync(path.join(root,'user-script.ahk'),'user file');const nsis=renderOwnedFiles(rows,'1.2.0');
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
test('updated invocation requires protocol-one exact per-user destination before any uninstall or install effects',()=>{
  const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
  const macro=(name:string)=>installer.slice(installer.indexOf('!macro '+name),installer.indexOf('!macroend',installer.indexOf('!macro '+name)));
  assert.match(macro('abortManualUpdate'),/SetErrorLevel 2[\s\S]*Abort/);
  const authorization=macro('validateUpdatedInstallation');
  assert.match(authorization,/ReadRegDWORD \$R1 HKCU "\$\{INSTALL_REGISTRY_KEY\}" "UpdateProtocol"[\s\S]*\$R1 != 1[\s\S]*abortManualUpdate/);
  assert.match(authorization,/GetDParameter \$R3[\s\S]*GetFullPathName \$R2 "\$R3"[\s\S]*\$R2 != \$ahkUpdateRoot/);
  assert.match(authorization,/ReadRegStr \$R0 HKCU "\$\{UNINSTALL_REGISTRY_KEY\}" "UninstallString"/);
  assert.match(authorization,/validateDirectoryChain[\s\S]*rejectRegisteredInstallation HKLM[\s\S]*SetRegView 32[\s\S]*rejectRegisteredInstallation HKLM[\s\S]*SetRegView 64/);
  assert.doesNotMatch(authorization,/rejectRegisteredInstallation HKCU/);
  assert.match(macro('preInit'),/\$\{If\} \$\{isUpdated\}[\s\S]*validateUpdatedInstallation "installer"[\s\S]*\$\{Else\}[\s\S]*rejectRegisteredInstallation HKCU[\s\S]*rejectRegisteredInstallation HKLM[\s\S]*prepareInstallerCache/);
  assert.match(macro('customUnInit'),/guardUninstallUpdate/);
  assert.match(macro('guardUninstallUpdate'),/\$\{If\} \$\{isUpdated\}[\s\S]*validateUpdatedInstallation "uninstaller"/);
  const uninstall=macro('customUnInstall');assert.ok(uninstall.indexOf('guardUninstallUpdate')<uninstall.indexOf('validateOwnedRemoval'));assert.ok(uninstall.indexOf('guardUninstallUpdate')<uninstall.indexOf('DeleteRegValue'));
  const generated=renderOwnedFiles([{relative:'app.exe',sha512:'a'.repeat(128),bytes:1}],'1.2.0');const removal=generated.slice(generated.indexOf('!macro customRemoveFiles'));
  assert.ok(removal.indexOf('guardUninstallUpdate')<removal.indexOf('validateOwnedRemoval'));assert.ok(removal.indexOf('guardUninstallUpdate')<removal.indexOf('Delete '));
  assert.doesNotMatch(installer+generated,/KillProcess|taskkill|RMDir \/r|Delete .*Updates|Delete .*installers/);
});

test('update uninstall preserves Run and administrative task while ordinary removal retains its guards',()=>{
  const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
  const uninstall=installer.slice(installer.indexOf('!macro customUnInstall'),installer.indexOf('!macroend',installer.indexOf('!macro customUnInstall')));
  const updatedBranch=uninstall.indexOf('${IfNot} ${isUpdated}');
  assert.ok(uninstall.indexOf('validateOwnedRemoval')<updatedBranch);
  assert.ok(updatedBranch<uninstall.indexOf('checkOwnedAdministrativeStartup'));
  assert.ok(updatedBranch<uninstall.indexOf('DeleteRegValue'));
  assert.doesNotMatch(uninstall,/DeleteTask|RegisterTask|schtasks|CompareExchangeTask/);
  assert.match(installer,/!macro customInstall[\s\S]*publishInstallerReceipt[\s\S]*WriteRegDWORD HKCU "\$\{INSTALL_REGISTRY_KEY\}" "UpdateProtocol" 1/);
});

test('installer version is a validated literal and cannot inject cache paths NSIS variables or JSON',()=>{
  for(const version of ['1.2.0','1.2.1-beta.2','1.2.0+build.42'])assert.equal(validateVersion(version),version);
  for(const version of [undefined,123,'','01.2.0','1.2','1.2.0-01','../1.2.0','1.2.0/$INSTDIR','1.2.0${APP_VERSION}','1.2.0"','1.2.0\nAbort','1.2.0\0'])assert.throws(()=>validateVersion(version),/Invalid installer version/);
  const body=renderOwnedFiles([{relative:'app.exe',sha512:'a'.repeat(128),bytes:1}],'1.2.3');
  assert.match(body,/!define APP_VERSION "1\.2\.3"/);
  assert.throws(()=>renderOwnedFiles([],'1.2.0$INSTDIR'),/Invalid installer version/);
});

test('packager version override controls APP_VERSION and cache receipt version for each independently built installer',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ahk-installer-version-'));
  try{
    const app=path.join(root,'app');fs.mkdirSync(app);fs.writeFileSync(path.join(app,'app.exe'),'owned fixture');
    const icon=path.join(root,'icon.ico');fs.writeFileSync(icon,'fixture icon');
    const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
    assert.match(installer,/version\$\\":\$\\"\$\{APP_VERSION\}/);
    for(const version of ['1.2.0','1.2.1']){
      const output=path.join(root,version);fs.mkdirSync(output);
      await writeOwnedFiles({appOutDir:app,packager:{appInfo:{version}}},output,icon);
      const header=fs.readFileSync(path.join(output,'owned-files.nsh'),'utf8');
      assert.ok(header.includes(`!define APP_VERSION "${version}"`));
      const manifest=JSON.parse(fs.readFileSync(path.join(output,'owned-files.json'),'utf8'));
      assert.equal(manifest.version,version);assert.equal(manifest.updateProtocol,1);
    }
    const invalid=path.join(root,'invalid');fs.mkdirSync(invalid);
    await assert.rejects(writeOwnedFiles({appOutDir:app},invalid,icon),/Invalid installer version/);
    assert.deepEqual(fs.readdirSync(invalid),[]);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

test('installer history copies exact self bytes without overwrite and publishes a verifiable receipt after install',()=>{
  const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
  const macro=(name:string)=>installer.slice(installer.indexOf('!macro '+name),installer.indexOf('!macroend',installer.indexOf('!macro '+name)));
  const prepare=macro('prepareInstallerCache'),publish=macro('publishInstallerReceipt');
  assert.match(prepare,/\$LOCALAPPDATA\\Bezoro\\AhkManagerElectron\\Updates\\installers\\\$\{APP_VERSION\}/);
  assert.match(prepare,/HashFile\} \$ahkInstallerHash "SHA2-256" "\$EXEPATH"/);
  assert.match(prepare,/FileSeek \$R0 0 END \$ahkInstallerBytes/);
  assert.match(prepare,/CopyFileW\(w "\$EXEPATH", w "\$ahkInstallerCacheFolder\\installer\.exe", i 1\)/);
  assert.match(prepare,/\$R2 != \$ahkInstallerHash/);
  assert.match(prepare,/schemaVersion[\s\S]*updateProtocol[\s\S]*version[\s\S]*sha256[\s\S]*bytes/);
  assert.match(prepare,/validateExistingInstallerReceipt/);
  assert.match(publish,/validateExistingInstallerReceipt/);
  assert.match(publish,/Rename "\$R4" "\$ahkInstallerCacheFolder\\receipt\.json"/);
  assert.doesNotMatch(prepare+publish,/Delete |RMDir|powershell|ExecutionPolicy/);
  assert.match(macro('customInstall'),/publishInstallerReceipt/);
  assert.doesNotMatch(macro('preInit'),/publishInstallerReceipt/);
});

test('uninstall checks owned administrative startup through the hash-verified read-only backend before effects',()=>{
  const installer=fs.readFileSync(path.resolve('scripts/installer.nsh'),'utf8');
  const uninstall=installer.slice(installer.indexOf('!macro customUnInstall'),installer.indexOf('!macroend',installer.indexOf('!macro customUnInstall')));
  assert.ok(uninstall.indexOf('validateOwnedRemoval')<uninstall.indexOf('checkOwnedAdministrativeStartup'));
  assert.ok(uninstall.indexOf('checkOwnedAdministrativeStartup')<uninstall.indexOf('DeleteRegValue'));
  const gate=installer.slice(installer.indexOf('!macro checkOwnedAdministrativeStartup'),installer.indexOf('!macroend',installer.indexOf('!macro checkOwnedAdministrativeStartup')));
  assert.match(gate,/"\$INSTDIR\\backend\\AhkManager\.Backend\.exe" --inspect-owned-startup/);
  assert.match(gate,/\$R2 == 2[\s\S]*Abort[\s\S]*\$R2 != 0[\s\S]*Abort/);
  const helper=fs.readFileSync(path.resolve('../Backend/StartupUninstallInspection.cs'),'utf8');
  const entry=fs.readFileSync(path.resolve('../Backend/Program.cs'),'utf8');
  assert.match(helper,/HostPath\(AppContext\.BaseDirectory\)/);
  assert.match(helper,/WindowsStartupTaskStore\.Read\(sid\)/);
  assert.match(helper,/UserStartupService\.OwnsTask/);
  assert.match(entry,/args\.Length == 1 \? StartupUninstallInspection\.Run\(\) : 3/);
  assert.ok(entry.indexOf('--inspect-owned-startup')<entry.indexOf('new ParentGuard'));
  assert.doesNotMatch(gate+helper,/powershell|ExecutionPolicy|CompareExchange|RegisterTask|DeleteTask|Process\.Start|new UserEffectOwner|BackendSession\.CreateProduction/);
});
