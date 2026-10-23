const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
function validateRelative(value){
  const relative=value.replaceAll('\\','/');
  if(!relative||relative.startsWith('/')||relative.split('/').some(part=>!part||part==='.'||part==='..'||!(/^[A-Za-z0-9._ ()-]+$/).test(part)||/[. ]$/.test(part)||/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)/i.test(part)))throw new Error('Unsafe owned package path');
  return relative;
}
const digest=file=>crypto.createHash('sha512').update(fs.readFileSync(file)).digest('hex');
function assertAncestorDirectories(root){
  let current=path.resolve(root);
  for(;;){
    const info=fs.lstatSync(current);
    if(info.isSymbolicLink()||!info.isDirectory())throw new Error('Package ancestor is redirected or not a directory');
    const parent=path.dirname(current);if(parent===current)return;current=parent;
  }
}
function collectOwnedFiles(root){
  const base=path.resolve(root),rows=[];
  assertAncestorDirectories(base);
  const walk=folder=>{
    const info=fs.lstatSync(folder);if(info.isSymbolicLink()||!info.isDirectory())throw new Error('Package directory is redirected');
    for(const entry of fs.readdirSync(folder,{withFileTypes:true})){
      const file=path.join(folder,entry.name),stat=fs.lstatSync(file);
      if(stat.isSymbolicLink())throw new Error('Package entry is redirected');
      if(stat.isDirectory())walk(file);
      else if(stat.isFile()&&stat.nlink===1)rows.push({relative:validateRelative(path.relative(base,file)),sha512:digest(file),bytes:stat.size});
      else throw new Error('Package entry is not an owned regular file');
    }
  };
  walk(base);return rows.sort((a,b)=>a.relative.localeCompare(b.relative,'en'));
}
function renderOwnedFiles(rows){
  const directories=new Set();
  for(const row of rows){validateRelative(row.relative);if(!/^[a-f0-9]{128}$/.test(row.sha512))throw new Error('Invalid owned file hash');const parts=row.relative.split('/');for(let i=1;i<parts.length;i++)directories.add(parts.slice(0,i).join('/'));}
  const q=relative=>`"$INSTDIR${relative?'\\'+relative.replaceAll('/','\\'):''}"`;
  const out=['; Generated from exact final packaged files. No recursive removal.','!include "FileFunc.nsh"','!macro validateOwnedRemoval',
    '  ClearErrors','  GetFullPathName $R6 "$INSTDIR"','  ${If} ${Errors}','    Abort "O caminho absoluto da instalação não pôde ser verificado."','  ${EndIf}',
    '  ${GetRoot} "$R6" $R7','  ${If} $R7 == ""','    Abort "A raiz do volume ou compartilhamento não pôde ser verificada."','  ${EndIf}',
    '  ${Do}','    ${If} $R6 == $R7',`      System::Call 'kernel32::GetFileAttributesW(w "$R6\\") i .r0'`,'    ${Else}',`      System::Call 'kernel32::GetFileAttributesW(w "$R6") i .r0'`,'    ${EndIf}',
    '    ${If} $0 == -1','      Abort "Um ancestral da instalação não pôde ser verificado."','    ${EndIf}',
    '    IntOp $R8 $0 & 0x400','    ${If} $R8 != 0','      Abort "Um ancestral da instalação foi redirecionado. Nenhum arquivo será removido."','    ${EndIf}',
    '    IntOp $R8 $0 & 0x10','    ${If} $R8 == 0','      Abort "Um ancestral da instalação não é uma pasta."','    ${EndIf}',
    '    ${If} $R6 == $R7','      ${Break}','    ${EndIf}',
    '    ${GetParent} "$R6" $R8','    StrLen $R9 $R8','    StrLen $R5 $R6','    ${If} $R9 == 0','    ${OrIf} $R9 >= $R5','      Abort "A cadeia de ancestrais da instalação não pôde ser verificada."','    ${EndIf}','    StrCpy $R6 $R8','  ${Loop}',
    `  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i .r0'`,'  ${If} $0 == -1','    Abort "A pasta da instalação não pôde ser verificada."','  ${EndIf}'];
  const guarded=['',...Array.from(directories).sort(),...rows.map(row=>row.relative)];
  for(const relative of guarded){
    out.push(`  System::Call 'kernel32::GetFileAttributesW(w ${q(relative)}) i .r0 ?e'`,'  Pop $2','  ${If} $0 == -1','    ${If} $2 != 2','    ${AndIf} $2 != 3','      Abort "Um caminho da instalação não pôde ser verificado."','    ${EndIf}','  ${Else}','    IntOp $1 $0 & 0x400','    ${If} $1 != 0','      Abort "Um caminho da instalação foi redirecionado. Nenhum arquivo será removido."','    ${EndIf}','    IntOp $1 $0 & 0x10',rows.some(row=>row.relative===relative)?'    ${If} $1 != 0':'    ${If} $1 == 0','      Abort "O tipo de um caminho da instalação foi alterado."','    ${EndIf}','  ${EndIf}');
  }
  for(const row of rows){
    const file=q(row.relative);
    out.push('  ${If} ${FileExists} '+file,'    ${StdUtils.HashFile} $2 "SHA2-512" '+file,'    ${If} $2 != "'+row.sha512+'"','      Abort "Um arquivo da instalação foi alterado. Nada será removido; preserve os dados e verifique a instalação."','    ${EndIf}','  ${EndIf}');
  }
  out.push(`  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR\\\${UNINSTALL_FILENAME}") i .r0 ?e'`,'  Pop $2','  ${If} $0 == -1','    ${If} $2 != 2','    ${AndIf} $2 != 3','      Abort "O caminho do desinstalador não pôde ser verificado."','    ${EndIf}','  ${EndIf}','  ${If} $0 != -1','    IntOp $1 $0 & 0x410','    ${If} $1 != 0','      Abort "O caminho do desinstalador foi redirecionado ou alterado."','    ${EndIf}','  ${EndIf}','  ${If} ${FileExists} "$INSTDIR\\${UNINSTALL_FILENAME}"','    ${StdUtils.HashFile} $2 "SHA2-512" "$INSTDIR\\${UNINSTALL_FILENAME}"','    ${StdUtils.HashFile} $3 "SHA2-512" "$EXEPATH"','    StrLen $4 $2','    StrLen $5 $3','    ${If} $4 != 128','    ${OrIf} $5 != 128','    ${OrIf} $2 != $3','      Abort "O desinstalador instalado não corresponde a esta execução."','    ${EndIf}','  ${EndIf}','!macroend','!macro customRemoveFiles','  !insertmacro rejectInPlaceUpdate','  !insertmacro validateOwnedRemoval');
  for(const row of rows)out.push('  ${If} ${FileExists} '+q(row.relative),'    ClearErrors',`    Delete ${q(row.relative)}`,'    ${If} ${Errors}','      Abort "Não foi possível remover um arquivo. A remoção está incompleta; os demais arquivos foram preservados."','    ${EndIf}','  ${EndIf}');
  out.push('  Delete "$INSTDIR\\${UNINSTALL_FILENAME}"','  SetOutPath $TEMP');
  for(const directory of Array.from(directories).sort((a,b)=>b.split('/').length-a.split('/').length||b.localeCompare(a)))out.push(`  RMDir ${q(directory)}`);
  out.push('  RMDir "$INSTDIR"','!macroend','');return out.join('\r\n');
}
async function writeOwnedFiles(context,output,uninstallerIcon){
  const rows=collectOwnedFiles(context.appOutDir);
  rows.push({relative:'uninstallerIcon.ico',sha512:digest(uninstallerIcon),bytes:fs.statSync(uninstallerIcon).size});
  const body=renderOwnedFiles(rows);
  fs.writeFileSync(path.join(output,'owned-files.nsh'),body,'utf8');
  fs.writeFileSync(path.join(output,'owned-files.json'),JSON.stringify({schema_version:1,files:rows,uninstaller:'same bytes as running $EXEPATH',policy:'Hash guard before removal; no reparse paths; exact files; empty directories only'},null,2));
}
module.exports={validateRelative,collectOwnedFiles,renderOwnedFiles,writeOwnedFiles};
