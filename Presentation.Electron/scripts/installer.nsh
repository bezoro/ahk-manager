; Never stop a process tree. A running Manager must complete its own bounded shutdown.
!include "$%AHK_PACKAGE_OUT%\owned-files.nsh"
!include "StrFunc.nsh"
!ifndef BUILD_UNINSTALLER
  ${Using:StrFunc} StrCase
!endif

Var /GLOBAL ahkUpdateRoot
Var /GLOBAL ahkInstallerCacheFolder
Var /GLOBAL ahkInstallerHash
Var /GLOBAL ahkInstallerBytes
Var /GLOBAL ahkInstallerReceipt

LangString ahkManualUpdate 1046 "Esta instalação exige atualização manual ou uma atualização autorizada pelo AHK Manager. Saia do Manager antes de continuar. A migração de versões anteriores ao protocolo de atualização 1 exige desinstalar preservando perfis e scripts e instalar a nova versão. Nenhum arquivo da instalação foi alterado por esta tentativa."

!macro abortManualUpdate
  MessageBox MB_OK|MB_ICONEXCLAMATION "$(ahkManualUpdate)" /SD IDOK
  SetErrorLevel 2
  Abort
!macroend

!macro abortInstallerCache
  MessageBox MB_OK|MB_ICONEXCLAMATION "Não foi possível preservar este instalador no histórico de versões. Nenhuma cópia existente será sobrescrita. Confira o acesso à pasta Updates/installers e tente novamente." /SD IDOK
  SetErrorLevel 3
  Abort
!macroend

!macro rejectRegisteredInstallation ROOT
  ReadRegStr $R0 ${ROOT} "${INSTALL_REGISTRY_KEY}" "InstallLocation"
  ${If} $R0 != ""
    !insertmacro abortManualUpdate
  ${EndIf}
  ReadRegStr $R0 ${ROOT} "${UNINSTALL_REGISTRY_KEY}" "UninstallString"
  ${If} $R0 != ""
    !insertmacro abortManualUpdate
  ${EndIf}
!macroend

; Missing ancestors are permitted only when preparing a new cache directory.
!macro validateDirectoryChain TARGET REQUIRE_EXISTING
  ClearErrors
  GetFullPathName $R6 "${TARGET}"
  ${If} ${Errors}
    !insertmacro abortManualUpdate
  ${EndIf}
  ${GetRoot} "$R6" $R7
  ${If} $R7 == ""
    !insertmacro abortManualUpdate
  ${EndIf}
  ${Do}
    ${If} $R6 == $R7
      System::Call 'kernel32::GetFileAttributesW(w "$R6\") i .r0 ?e'
    ${Else}
      System::Call 'kernel32::GetFileAttributesW(w "$R6") i .r0 ?e'
    ${EndIf}
    Pop $2
    ${If} $0 == -1
      !if ${REQUIRE_EXISTING} == 1
        !insertmacro abortManualUpdate
      !else
        ${If} $2 != 2
        ${AndIf} $2 != 3
          !insertmacro abortManualUpdate
        ${EndIf}
      !endif
    ${Else}
      IntOp $R8 $0 & 0x400
      ${If} $R8 != 0
        !insertmacro abortManualUpdate
      ${EndIf}
      IntOp $R8 $0 & 0x10
      ${If} $R8 == 0
        !insertmacro abortManualUpdate
      ${EndIf}
    ${EndIf}
    ${If} $R6 == $R7
      ${Break}
    ${EndIf}
    ${GetParent} "$R6" $R8
    StrLen $R9 $R8
    StrLen $R5 $R6
    ${If} $R9 == 0
    ${OrIf} $R9 >= $R5
      !insertmacro abortManualUpdate
    ${EndIf}
    StrCpy $R6 $R8
  ${Loop}
!macroend

!macro validateUpdatedInstallation MODE
  SetRegView 64
  ClearErrors
  ReadRegDWORD $R1 HKCU "${INSTALL_REGISTRY_KEY}" "UpdateProtocol"
  ${If} ${Errors}
  ${OrIf} $R1 != 1
    !insertmacro abortManualUpdate
  ${EndIf}
  ReadRegStr $R0 HKCU "${INSTALL_REGISTRY_KEY}" "InstallLocation"
  ${If} $R0 == ""
    !insertmacro abortManualUpdate
  ${EndIf}
  ClearErrors
  GetFullPathName $ahkUpdateRoot "$R0"
  ${If} ${Errors}
  ${OrIf} $ahkUpdateRoot != $R0
    !insertmacro abortManualUpdate
  ${EndIf}
  !if "${MODE}" == "installer"
    !insertmacro GetDParameter $R3
    ${If} $R3 == ""
      !insertmacro abortManualUpdate
    ${EndIf}
  !else
    StrCpy $R3 "$INSTDIR"
  !endif
  ClearErrors
  GetFullPathName $R2 "$R3"
  ${If} ${Errors}
  ${OrIf} $R2 != $ahkUpdateRoot
    !insertmacro abortManualUpdate
  ${EndIf}
  StrCpy $INSTDIR "$ahkUpdateRoot"
  ReadRegStr $R0 HKCU "${UNINSTALL_REGISTRY_KEY}" "UninstallString"
  StrCpy $R1 '$\"$ahkUpdateRoot\${UNINSTALL_FILENAME}$\" /currentuser'
  ${If} $R0 != $R1
    !insertmacro abortManualUpdate
  ${EndIf}
  !insertmacro validateDirectoryChain "$ahkUpdateRoot" 1
  !insertmacro rejectRegisteredInstallation HKLM
  SetRegView 32
  !insertmacro rejectRegisteredInstallation HKCU
  !insertmacro rejectRegisteredInstallation HKLM
  SetRegView 64
!macroend

!macro guardUninstallUpdate
  ${If} ${isUpdated}
    !insertmacro validateUpdatedInstallation "uninstaller"
  ${EndIf}
!macroend

!macro validateCacheFile TARGET
  System::Call 'kernel32::GetFileAttributesW(w "${TARGET}") i .r0 ?e'
  Pop $2
  ${If} $0 == -1
    ${If} $2 != 2
    ${AndIf} $2 != 3
      !insertmacro abortInstallerCache
    ${EndIf}
  ${Else}
    IntOp $1 $0 & 0x410
    ${If} $1 != 0
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
!macroend

!macro validateExistingInstallerReceipt
  !insertmacro validateCacheFile "$ahkInstallerCacheFolder\receipt.json"
  ${If} ${FileExists} "$ahkInstallerCacheFolder\receipt.json"
    ClearErrors
    FileOpen $R0 "$ahkInstallerCacheFolder\receipt.json" r
    ${If} ${Errors}
      !insertmacro abortInstallerCache
    ${EndIf}
    FileSeek $R0 0 END $R2
    FileSeek $R0 0 SET
    FileRead $R0 $R1
    FileClose $R0
    StrLen $R3 $ahkInstallerReceipt
    ${If} ${Errors}
    ${OrIf} $R1 != $ahkInstallerReceipt
    ${OrIf} $R2 != $R3
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
!macroend

!macro prepareInstallerCache
  StrCpy $ahkInstallerCacheFolder "$LOCALAPPDATA\Bezoro\AhkManagerElectron\Updates\installers\${APP_VERSION}"
  !insertmacro validateDirectoryChain "$ahkInstallerCacheFolder" 0
  ClearErrors
  CreateDirectory "$ahkInstallerCacheFolder"
  ${If} ${Errors}
    !insertmacro abortInstallerCache
  ${EndIf}
  !insertmacro validateDirectoryChain "$ahkInstallerCacheFolder" 1
  ${StdUtils.HashFile} $ahkInstallerHash "SHA2-256" "$EXEPATH"
  StrLen $R0 $ahkInstallerHash
  ${If} $R0 != 64
    !insertmacro abortInstallerCache
  ${EndIf}
  ${StrCase} $ahkInstallerHash "$ahkInstallerHash" "L"
  ClearErrors
  FileOpen $R0 "$EXEPATH" r
  ${If} ${Errors}
    !insertmacro abortInstallerCache
  ${EndIf}
  FileSeek $R0 0 END $ahkInstallerBytes
  FileClose $R0
  ${If} ${Errors}
  ${OrIf} $ahkInstallerBytes <= 0
    !insertmacro abortInstallerCache
  ${EndIf}
  !insertmacro validateCacheFile "$ahkInstallerCacheFolder\installer.exe"
  ${IfNot} ${FileExists} "$ahkInstallerCacheFolder\installer.exe"
    ; Fail-if-exists prevents overwriting a foreign or concurrently created file.
    System::Call 'kernel32::CopyFileW(w "$EXEPATH", w "$ahkInstallerCacheFolder\installer.exe", i 1) i .r0'
    ${If} $0 == 0
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
  ${StdUtils.HashFile} $R2 "SHA2-256" "$ahkInstallerCacheFolder\installer.exe"
  ${If} $R2 != $ahkInstallerHash
    !insertmacro abortInstallerCache
  ${EndIf}
  StrCpy $ahkInstallerReceipt '{$\"schemaVersion$\":1,$\"updateProtocol$\":1,$\"version$\":$\"${APP_VERSION}$\",$\"sha256$\":$\"$ahkInstallerHash$\",$\"bytes$\":$ahkInstallerBytes}'
  !insertmacro validateExistingInstallerReceipt
!macroend

; Publish a receipt only after installation succeeds. Both files survive uninstall.
!macro publishInstallerReceipt
  !insertmacro validateDirectoryChain "$ahkInstallerCacheFolder" 1
  !insertmacro validateCacheFile "$ahkInstallerCacheFolder\installer.exe"
  ${StdUtils.HashFile} $R2 "SHA2-256" "$ahkInstallerCacheFolder\installer.exe"
  ${If} $R2 != $ahkInstallerHash
    !insertmacro abortInstallerCache
  ${EndIf}
  !insertmacro validateExistingInstallerReceipt
  ${IfNot} ${FileExists} "$ahkInstallerCacheFolder\receipt.json"
    GetTempFileName $R4 "$ahkInstallerCacheFolder"
    ClearErrors
    FileOpen $R0 "$R4" w
    ${If} ${Errors}
      !insertmacro abortInstallerCache
    ${EndIf}
    FileWrite $R0 "$ahkInstallerReceipt"
    FileClose $R0
    ${If} ${Errors}
      !insertmacro abortInstallerCache
    ${EndIf}
    ClearErrors
    Rename "$R4" "$ahkInstallerCacheFolder\receipt.json"
    ${If} ${Errors}
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
!macroend

; preInit precedes uninstallOldVersion and all installation file/registry effects.
!macro preInit
  !ifndef BUILD_UNINSTALLER
    ${If} ${isUpdated}
      !insertmacro validateUpdatedInstallation "installer"
    ${Else}
      SetRegView 64
      !insertmacro rejectRegisteredInstallation HKCU
      !insertmacro rejectRegisteredInstallation HKLM
      SetRegView 32
      !insertmacro rejectRegisteredInstallation HKCU
      !insertmacro rejectRegisteredInstallation HKLM
      SetRegView 64
    ${EndIf}
    !insertmacro prepareInstallerCache
  !endif
!macroend

!macro customInstall
  !insertmacro publishInstallerReceipt
  SetRegView 64
  ClearErrors
  WriteRegDWORD HKCU "${INSTALL_REGISTRY_KEY}" "UpdateProtocol" 1
  ${If} ${Errors}
    !insertmacro abortInstallerCache
  ${EndIf}
!macroend

!macro customUnInit
  !insertmacro guardUninstallUpdate
!macroend

!macro customCheckAppRunning
  nsProcess::_FindProcess "${APP_EXECUTABLE_FILENAME}"
  Pop $R0
  ${If} $R0 == 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "Encerre o AHK Manager pela opção Sair antes de instalar ou remover. Os scripts AutoHotkey podem continuar em execução." /SD IDOK
    Abort
  ${EndIf}
!macroend

!macro checkOwnedAdministrativeStartup
  ; validateOwnedRemoval has already verified this exact backend and its paths.
  nsExec::ExecToStack /TIMEOUT=15000 '"$INSTDIR\backend\AhkManager.Backend.exe" --inspect-owned-startup'
  Pop $R2
  Pop $R3
  ${If} $R2 == 2
    Abort "Este AHK Manager ainda possui uma tarefa de início com Windows. Abra o Manager como administrador, desative Iniciar com Windows nas Preferências, saia do Manager e execute a desinstalação novamente. Nenhum arquivo foi removido."
  ${ElseIf} $R2 != 0
    Abort "Não foi possível conferir a tarefa de início com Windows. Nenhum arquivo foi removido. Verifique o acesso ao Agendador de Tarefas antes de tentar novamente."
  ${EndIf}
!macroend

!macro customUnInstall
  !insertmacro guardUninstallUpdate
  ; Update and ordinary removal use the same exact ownership and hash checks.
  !insertmacro validateOwnedRemoval
  ${IfNot} ${isUpdated}
    !insertmacro checkOwnedAdministrativeStartup
    ReadRegStr $R0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
    StrCpy $R1 '$\"$INSTDIR\Bezoro.AhkManager.exe$\" --login --background'
    ${If} $R0 == $R1
      MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION "Remover a entrada de inicialização deste AHK Manager? Seu perfil e seus scripts serão preservados." /SD IDNO IDNO preserveStartup
      ReadRegStr $R0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
      ${If} $R0 == $R1
        DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
      ${EndIf}
    ${EndIf}
  ${EndIf}
  preserveStartup:
!macroend
