; Never stop a process tree. A running Manager must complete its own bounded shutdown.
!include "$%AHK_PACKAGE_OUT%\owned-files.nsh"
!define AHK_STARTUP_TASK_INSPECTOR "${__FILEDIR__}\Check-StartupTask.ps1"

LangString ahkManualUpdate 1046 "A atualização no mesmo local está desabilitada. Saia do AHK Manager, desinstale a versão anterior preservando perfis e scripts e instale a nova versão. Depois verifique a preferência de início com Windows, se a utilizava. Nenhum arquivo da instalação será alterado por esta tentativa."

!macro abortManualUpdate
  MessageBox MB_OK|MB_ICONEXCLAMATION "$(ahkManualUpdate)"
  SetErrorLevel 2
  Abort
!macroend

!macro rejectInPlaceUpdate
  ${If} ${isUpdated}
    !insertmacro abortManualUpdate
  ${EndIf}
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

; preInit precedes initMultiUser and the install section's uninstallOldVersion,
; extraction, registration and shortcut writes. Never dispatch an old uninstaller.
!macro preInit
  !ifndef BUILD_UNINSTALLER
    !insertmacro rejectInPlaceUpdate
    SetRegView 64
    !insertmacro rejectRegisteredInstallation HKCU
    !insertmacro rejectRegisteredInstallation HKLM
    SetRegView 32
    !insertmacro rejectRegisteredInstallation HKCU
    !insertmacro rejectRegisteredInstallation HKLM
    SetRegView 64
  !endif
!macroend

!macro customUnInit
  !insertmacro rejectInPlaceUpdate
!macroend
!macro customCheckAppRunning
  nsProcess::_FindProcess "${APP_EXECUTABLE_FILENAME}"
  Pop $R0
  ${If} $R0 == 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "Encerre o AHK Manager pela opção Sair antes de instalar ou remover. Os scripts AutoHotkey podem continuar em execução."
    Abort
  ${EndIf}
!macroend

!macro checkOwnedAdministrativeStartup
  ; Fixed helper only inspects COM. A policy or access failure blocks removal;
  ; there is no ExecutionPolicy bypass, task deletion or fabricated product flag.
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Check-StartupTask.ps1 "${AHK_STARTUP_TASK_INSPECTOR}"
  nsExec::ExecToStack /TIMEOUT=15000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -File "$PLUGINSDIR\Check-StartupTask.ps1" -ManagerExecutable "$INSTDIR\Bezoro.AhkManager.exe"'
  Pop $R2
  Pop $R3
  SetOutPath "$INSTDIR"
  ${If} $R2 == 2
    Abort "Este AHK Manager ainda possui uma tarefa de início com Windows. Abra o Manager como administrador, desative Iniciar com Windows nas Preferências, saia do Manager e execute a desinstalação novamente. Nenhum arquivo foi removido."
  ${ElseIf} $R2 != 0
    Abort "Não foi possível conferir a tarefa de início com Windows. Nenhum arquivo foi removido. Verifique o acesso ao Agendador de Tarefas e a política de execução do PowerShell antes de tentar novamente."
  ${EndIf}
!macroend

!macro customUnInstall
  !insertmacro rejectInPlaceUpdate
  ; Ownership, file hashes and every directory are checked before registry/file effects.
  !insertmacro validateOwnedRemoval
  !insertmacro checkOwnedAdministrativeStartup
  ; User profile, migration backups and scripts are never removed here.
  ReadRegStr $R0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
  StrCpy $R1 '$\"$INSTDIR\Bezoro.AhkManager.exe$\" --login --background'
  ${If} $R0 == $R1
    MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION "Remover a entrada de inicialização deste AHK Manager? Seu perfil e seus scripts serão preservados." /SD IDNO IDNO preserveStartup
    ReadRegStr $R0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
    ${If} $R0 == $R1
      DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
    ${EndIf}
  ${EndIf}
  preserveStartup:
!macroend
