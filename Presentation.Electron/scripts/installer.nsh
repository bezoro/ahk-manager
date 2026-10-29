; Never stop a process tree. A running Manager must complete its own bounded shutdown.
!include "$%AHK_PACKAGE_OUT%\owned-files.nsh"
!include "StrFunc.nsh"
!ifndef BUILD_UNINSTALLER
  ${StrCase}
!endif

Var /GLOBAL ahkUpdateRoot
!ifndef BUILD_UNINSTALLER
Var /GLOBAL ahkInstallerCacheFolder
Var /GLOBAL ahkInstallerHash
Var /GLOBAL ahkInstallerBytes
Var /GLOBAL ahkInstallerReceipt
Var /GLOBAL ahkRepairRoot
Var /GLOBAL ahkRepairExitCode
!endif

; Local stage diagnostics only. Logging cannot change a guard's registers/error flag.
; Reserve up to four bytes per character so each version's log stays within 32 KiB.
!macro traceInstaller STAGE
  !ifndef BUILD_UNINSTALLER
    Push $R3
    Push $R4
    Push $R5
    Push $R6
    StrCpy $R3 0
    ${If} ${Errors}
      StrCpy $R3 1
    ${EndIf}
    ClearErrors
    FileOpen $R4 "$TEMP\AHK-Manager-${APP_VERSION}-install.log" a
    ${IfNot} ${Errors}
      FileSeek $R4 0 END $R5
      ${IfNot} ${Errors}
      ${AndIf} $R5 >= 0
      ${AndIf} $R5 <= 32768
        StrLen $R6 "${STAGE}$\r$\n"
        IntOp $R6 $R6 * 4
        IntOp $R5 $R5 + $R6
        ${If} $R5 <= 32768
          FileWrite $R4 "${STAGE}$\r$\n"
        ${EndIf}
      ${EndIf}
      FileClose $R4
    ${EndIf}
    ClearErrors
    ${If} $R3 == 1
      SetErrors
    ${EndIf}
    Pop $R6
    Pop $R5
    Pop $R4
    Pop $R3
  !endif
!macroend

LangString ahkManualUpdate 1033 "This installation requires a manual update or an update authorized by AHK Manager. Exit the manager before continuing. Migrating versions older than update protocol 1 requires uninstalling while preserving profiles and scripts, then installing the new version. This attempt did not change any installation files."

!macro abortManualUpdate
  !insertmacro traceInstaller "abort: manual-update; exit=2"
  MessageBox MB_OK|MB_ICONEXCLAMATION "$(ahkManualUpdate)" /SD IDOK
  SetErrorLevel 2
  Abort
!macroend

!macro abortInstallerCache
  !insertmacro traceInstaller "abort: installer-cache; exit=3"
  MessageBox MB_OK|MB_ICONEXCLAMATION "This installer could not be preserved in version history. No existing copy will be overwritten. Check access to the Updates/installers folder and try again." /SD IDOK
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
  !if ${REQUIRE_EXISTING} == 0
    ; NSIS GetFullPathName rejects a missing leaf; the Win32 API normalizes it.
    System::Call 'kernel32::GetFullPathNameW(w "${TARGET}", i ${NSIS_MAX_STRLEN}, w .r1, p 0) i .r0 ?e'
    Pop $2
    ${If} $0 == 0
    ${OrIf} $0 >= ${NSIS_MAX_STRLEN}
      !insertmacro traceInstaller "directory-chain: canonicalization-failed; length=$0; win32=$2"
      !insertmacro abortManualUpdate
    ${EndIf}
    StrCpy $R6 $1
  !else
    GetFullPathName $R6 "${TARGET}"
    ${If} ${Errors}
      !insertmacro abortManualUpdate
    ${EndIf}
  !endif
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
        !insertmacro traceInstaller "directory-chain: missing; attrs=$0; win32=$2"
        !insertmacro abortManualUpdate
      !else
        ${If} $2 != 2
        ${AndIf} $2 != 3
          !insertmacro traceInstaller "directory-chain: query-failed; attrs=$0; win32=$2"
          !insertmacro abortManualUpdate
        ${EndIf}
      !endif
    ${Else}
      IntOp $R8 $0 & 0x400
      ${If} $R8 != 0
        !insertmacro traceInstaller "directory-chain: redirected; attrs=$0; win32=$2"
        !insertmacro abortManualUpdate
      ${EndIf}
      IntOp $R8 $0 & 0x10
      ${If} $R8 == 0
        !insertmacro traceInstaller "directory-chain: not-directory; attrs=$0; win32=$2"
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
    !if "${MODE}" == "repair"
      StrCpy $R3 "$ahkUpdateRoot"
    !else
      StrCpy $R3 "$INSTDIR"
    !endif
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
  ; HKCU\Software is shared across views on supported Windows versions.
  ; The same per-user installation was validated exactly above.
  !insertmacro rejectRegisteredInstallation HKLM
  SetRegView 64
!macroend

; The parent has not acquired electron-builder's installer mutex yet. Validate
; ownership, obtain consent, and delegate to the existing exact update path.
!macro repairRegisteredInstallation
  !insertmacro validateUpdatedInstallation "repair"
  StrCpy $ahkRepairRoot "$ahkUpdateRoot"
  ${StdUtils.TestParameter} $R0 "repair"
  ${If} $R0 != "true"
    ${If} ${Silent}
      !insertmacro abortManualUpdate
    ${EndIf}
    SetErrorLevel 2
    !insertmacro customCheckAppRunning
    SetErrorLevel 0
    MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION "Update or repair this AHK Manager installation?$\r$\n$\r$\n$ahkRepairRoot$\r$\n$\r$\nClose the manager first. Your profile, scripts, version history, and Windows startup settings will be preserved. Existing file ownership checks remain in effect." /SD IDNO IDYES ahkRepairApproved
    SetErrorLevel 2
    Quit
    ahkRepairApproved:
  ${EndIf}
  ; Registry or path changes while confirmation was open invalidate consent.
  !insertmacro validateUpdatedInstallation "repair"
  ${If} $ahkUpdateRoot != $ahkRepairRoot
    !insertmacro abortManualUpdate
  ${EndIf}
  SetErrorLevel 2
  !insertmacro customCheckAppRunning
  SetErrorLevel 0
  !insertmacro traceInstaller "repair: delegate verified root=$ahkRepairRoot"
  StrCpy $ahkRepairExitCode 2
  ClearErrors
  ; NSIS consumes the unquoted remainder after /D=. It must stay last.
  ${If} ${Silent}
    ExecWait '$\"$EXEPATH$\" /S --updated /D=$ahkRepairRoot' $ahkRepairExitCode
  ${Else}
    ExecWait '$\"$EXEPATH$\" --updated /D=$ahkRepairRoot' $ahkRepairExitCode
  ${EndIf}
  ${If} ${Errors}
    !insertmacro traceInstaller "repair: child could not be started"
    SetErrorLevel 2
  ${Else}
    !insertmacro traceInstaller "repair: child exit=$ahkRepairExitCode"
    SetErrorLevel $ahkRepairExitCode
  ${EndIf}
  ; The parent must never enter cache, uninstall, or installation stages.
  Quit
!macroend

; The assisted installer otherwise appends APP_FILENAME to a custom destination
; after preInit, even when the --updated directory page has been skipped.
!macro customPageAfterChangeDir
  !ifdef MUI_PAGE_CUSTOMFUNCTION_PRE
    !undef MUI_PAGE_CUSTOMFUNCTION_PRE
  !endif
  !define MUI_PAGE_CUSTOMFUNCTION_PRE ahkInstallFilesPre
  Function ahkInstallFilesPre
    ${If} ${isUpdated}
      !insertmacro validateUpdatedInstallation "installer"
    ${Else}
      !ifdef allowToChangeInstallationDirectory
        Call instFilesPre
      !endif
    ${EndIf}
  FunctionEnd
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
      !insertmacro traceInstaller "cache-file: query-failed; attrs=$0; win32=$2"
      !insertmacro abortInstallerCache
    ${EndIf}
  ${Else}
    IntOp $1 $0 & 0x410
    ${If} $1 != 0
      !insertmacro traceInstaller "cache-file: unexpected-type; attrs=$0; win32=$2"
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
!macroend

!macro validateExistingInstallerReceipt
  !insertmacro traceInstaller "receipt: inspect-file"
  !insertmacro validateCacheFile "$ahkInstallerCacheFolder\receipt.json"
  ${If} ${FileExists} "$ahkInstallerCacheFolder\receipt.json"
    !insertmacro traceInstaller "receipt: compare-existing"
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
  !insertmacro traceInstaller "cache: path=$ahkInstallerCacheFolder"
  !insertmacro traceInstaller "cache: inspect-create-ancestors"
  !insertmacro validateDirectoryChain "$ahkInstallerCacheFolder" 0
  !insertmacro traceInstaller "cache: create-directory"
  ClearErrors
  CreateDirectory "$ahkInstallerCacheFolder"
  ${If} ${Errors}
    !insertmacro abortInstallerCache
  ${EndIf}
  !insertmacro traceInstaller "cache: verify-created-directory"
  !insertmacro validateDirectoryChain "$ahkInstallerCacheFolder" 1
  !insertmacro traceInstaller "cache: hash-source-installer"
  ${StdUtils.HashFile} $ahkInstallerHash "SHA2-256" "$EXEPATH"
  StrLen $R0 $ahkInstallerHash
  ${If} $R0 != 64
    !insertmacro abortInstallerCache
  ${EndIf}
  ${StrCase} $ahkInstallerHash "$ahkInstallerHash" "L"
  !insertmacro traceInstaller "cache: read-source-length"
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
  !insertmacro traceInstaller "cache: inspect-existing-copy"
  !insertmacro validateCacheFile "$ahkInstallerCacheFolder\installer.exe"
  ${IfNot} ${FileExists} "$ahkInstallerCacheFolder\installer.exe"
    ; Fail-if-exists prevents overwriting a foreign or concurrently created file.
    !insertmacro traceInstaller "cache: copy-installer"
    System::Call 'kernel32::CopyFileW(w "$EXEPATH", w "$ahkInstallerCacheFolder\installer.exe", i 1) i .r0'
    ${If} $0 == 0
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
  !insertmacro traceInstaller "cache: hash-cached-installer"
  ${StdUtils.HashFile} $R2 "SHA2-256" "$ahkInstallerCacheFolder\installer.exe"
  ${If} $R2 != $ahkInstallerHash
    !insertmacro abortInstallerCache
  ${EndIf}
  StrCpy $ahkInstallerReceipt '{$\"schemaVersion$\":1,$\"updateProtocol$\":1,$\"version$\":$\"${APP_VERSION}$\",$\"sha256$\":$\"$ahkInstallerHash$\",$\"bytes$\":$ahkInstallerBytes}'
  !insertmacro validateExistingInstallerReceipt
  !insertmacro traceInstaller "cache: prepared"
!macroend

; Publish a receipt only after installation succeeds. Both files survive uninstall.
!macro publishInstallerReceipt
  !insertmacro traceInstaller "receipt: publish-start"
  !insertmacro validateDirectoryChain "$ahkInstallerCacheFolder" 1
  !insertmacro traceInstaller "receipt: verify-installer-file"
  !insertmacro validateCacheFile "$ahkInstallerCacheFolder\installer.exe"
  !insertmacro traceInstaller "receipt: hash-installer-file"
  ${StdUtils.HashFile} $R2 "SHA2-256" "$ahkInstallerCacheFolder\installer.exe"
  ${If} $R2 != $ahkInstallerHash
    !insertmacro abortInstallerCache
  ${EndIf}
  !insertmacro validateExistingInstallerReceipt
  ${IfNot} ${FileExists} "$ahkInstallerCacheFolder\receipt.json"
    !insertmacro traceInstaller "receipt: write-temporary"
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
    !insertmacro traceInstaller "receipt: publish-rename"
    ClearErrors
    Rename "$R4" "$ahkInstallerCacheFolder\receipt.json"
    ${If} ${Errors}
      !insertmacro abortInstallerCache
    ${EndIf}
  ${EndIf}
  !insertmacro traceInstaller "receipt: published"
!macroend

; preInit precedes uninstallOldVersion and all installation file/registry effects.
!macro preInit
  !ifndef BUILD_UNINSTALLER
    !insertmacro traceInstaller "preInit: begin; version=${APP_VERSION}"
    ${If} ${isUpdated}
      !insertmacro traceInstaller "preInit: validate-update"
      !insertmacro validateUpdatedInstallation "installer"
    ${Else}
      SetRegView 64
      ReadRegStr $R0 HKCU "${INSTALL_REGISTRY_KEY}" "InstallLocation"
      ${StdUtils.TestParameter} $R1 "repair"
      ${If} $R0 == ""
      ${AndIf} $R1 == "true"
        !insertmacro abortManualUpdate
      ${EndIf}
      ${If} $R0 != ""
        !insertmacro repairRegisteredInstallation
      ${EndIf}
      !insertmacro traceInstaller "fresh-gate: HKCU64"
      SetRegView 64
      !insertmacro rejectRegisteredInstallation HKCU
      !insertmacro traceInstaller "fresh-gate: HKLM64"
      !insertmacro rejectRegisteredInstallation HKLM
      !insertmacro traceInstaller "fresh-gate: HKCU32"
      SetRegView 32
      !insertmacro rejectRegisteredInstallation HKCU
      !insertmacro traceInstaller "fresh-gate: HKLM32"
      !insertmacro rejectRegisteredInstallation HKLM
      SetRegView 64
    ${EndIf}
    !insertmacro prepareInstallerCache
    !insertmacro traceInstaller "preInit: complete"
  !endif
!macroend

!macro customInstall
  !insertmacro traceInstaller "customInstall: begin"
  !insertmacro publishInstallerReceipt
  !insertmacro traceInstaller "customInstall: register-protocol"
  SetRegView 64
  ClearErrors
  WriteRegDWORD HKCU "${INSTALL_REGISTRY_KEY}" "UpdateProtocol" 1
  ${If} ${Errors}
    !insertmacro abortInstallerCache
  ${EndIf}
  !insertmacro traceInstaller "customInstall: complete"
!macroend

!macro customUnInit
  !insertmacro guardUninstallUpdate
!macroend

!macro customCheckAppRunning
  nsProcess::_FindProcess "${APP_EXECUTABLE_FILENAME}"
  Pop $R0
  ${If} $R0 == 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "Use Exit to close AHK Manager before installing or uninstalling. AutoHotkey scripts may keep running." /SD IDOK
    Abort
  ${EndIf}
!macroend

!macro checkOwnedAdministrativeStartup
  ; validateOwnedRemoval has already verified this exact backend and its paths.
  nsExec::ExecToStack /TIMEOUT=15000 '"$INSTDIR\backend\AhkManager.Backend.exe" --inspect-owned-startup'
  Pop $R2
  Pop $R3
  ${If} $R2 == 2
    Abort "This AHK Manager installation still owns a Windows startup task. Open the manager as administrator, disable Start with Windows in Preferences, exit the manager, and run uninstall again. No files were removed."
  ${ElseIf} $R2 != 0
    Abort "The Windows startup task could not be verified. No files were removed. Check access to Task Scheduler before trying again."
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
      MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION "Remove the startup entry for this AHK Manager installation? Your profile and scripts will be preserved." /SD IDNO IDNO preserveStartup
      ReadRegStr $R0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
      ${If} $R0 == $R1
        DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Bezoro.AhkManager.WinUI"
      ${EndIf}
    ${EndIf}
  ${EndIf}
  preserveStartup:
!macroend
