!include "LogicLib.nsh"

Var cortexFinalDirectory
Var cortexNewDirectory
Var cortexOldDirectory
Var cortexOldMoved
Var cortexNewMoved

!macro cortexExtractPayload FILE
  !ifmacrodef customInstallerExtract
    !insertmacro customInstallerExtract "${FILE}"
  !else
    nsExec::ExecToStack '"$PLUGINSDIR\cortex-7za.exe" x -y -bd -bb0 "-o$INSTDIR" "${FILE}"'
    Pop $R0
    Pop $R1
  !endif
  ${If} $R0 != 0
    DetailPrint $R1
    Call cortexRollbackDirectories
    !ifmacrodef customInstallerExtractFailed
      !insertmacro customInstallerExtractFailed "${FILE}"
    !else
      MessageBox MB_OK|MB_ICONEXCLAMATION "$(decompressionFailed)" /SD IDOK
    !endif
    SetErrorLevel 2
    Quit
  ${EndIf}
!macroend

!macro cortexStageApplication
  StrCpy $cortexFinalDirectory $INSTDIR
  System::Call 'ole32::CoCreateGuid(g .r0) i .r1'
  ${If} $1 != 0
    SetErrorLevel 2
    Quit
  ${EndIf}
  StrCpy $cortexNewDirectory "$INSTDIR.new-$0"
  StrCpy $cortexOldDirectory "$INSTDIR.old-$0"
  StrCpy $cortexOldMoved ""
  StrCpy $cortexNewMoved ""
  ClearErrors
  CreateDirectory $cortexNewDirectory
  ${If} ${Errors}
    SetErrorLevel 2
    Quit
  ${EndIf}
  File /oname=$PLUGINSDIR\cortex-7za.exe "${CORTEX_SEVENZIP_PATH}"
  StrCpy $INSTDIR $cortexNewDirectory
  SetOutPath $INSTDIR
  !insertmacro installApplicationFiles
  !ifdef CORTEX_SEVENZIP_LICENSE_DIR
    File /oname=7zip-installer-LICENSE.txt "${CORTEX_SEVENZIP_LICENSE_DIR}\LICENSE.txt"
    File /oname=7zip-installer-COPYING.txt "${CORTEX_SEVENZIP_LICENSE_DIR}\COPYING"
  !endif
  !ifdef UNINSTALLER_ICON
    File /oname=uninstallerIcon.ico "${UNINSTALLER_ICON}"
  !endif
  StrCpy $INSTDIR $cortexFinalDirectory
  SetOutPath $PLUGINSDIR
!macroend

Function .onGUIEnd
  Call cortexCleanupDirectories
FunctionEnd

Function cortexCleanupDirectories
  ${If} $cortexFinalDirectory != ""
    Call cortexRollbackDirectories
  ${EndIf}
FunctionEnd

; Only directories created or renamed by this installer are removed during rollback.
Function cortexRollbackDirectories
  SetOutPath $PLUGINSDIR
  ${If} $cortexNewMoved == "1"
    RMDir /r "\\?\$cortexFinalDirectory"
    StrCpy $cortexNewMoved ""
  ${EndIf}
  ${If} $cortexOldMoved == "1"
    ClearErrors
    Rename $cortexOldDirectory $cortexFinalDirectory
    ${If} ${Errors}
      ; Leave the complete backup in place if another process prevents restoration.
      DetailPrint $cortexOldDirectory
      Return
    ${EndIf}
    StrCpy $cortexOldMoved ""
  ${EndIf}
  ${If} $cortexNewDirectory != ""
    RMDir /r "\\?\$cortexNewDirectory"
  ${EndIf}
  StrCpy $INSTDIR $cortexFinalDirectory
FunctionEnd

Function cortexPromoteDirectories
  !ifmacrodef InstallerPublishStage
    !insertmacro InstallerPublishStage 2
  !endif
  ; SetOutPath opens a directory handle; release it before either rename.
  SetOutPath $PLUGINSDIR
  ClearErrors
  ${If} ${FileExists} "$cortexFinalDirectory\*.*"
    Rename $cortexFinalDirectory $cortexOldDirectory
    ${If} ${Errors}
      Call cortexRollbackDirectories
      SetErrors
      Return
    ${EndIf}
    StrCpy $cortexOldMoved "1"
  ${Else}
    ; NSIS can create the destination before the install section starts.
    RMDir $cortexFinalDirectory
  ${EndIf}
  ClearErrors
  Rename $cortexNewDirectory $cortexFinalDirectory
  ${If} ${Errors}
    Call cortexRollbackDirectories
    SetErrors
    Return
  ${EndIf}
  StrCpy $cortexNewMoved "1"
  SetOutPath $cortexFinalDirectory
  !ifmacrodef InstallerPublishStage
    !insertmacro InstallerPublishStage 3
  !endif
  ClearErrors
FunctionEnd

!macro cortexFinishDirectories
  StrCpy $cortexNewMoved ""
  ${If} $cortexOldMoved == "1"
    RMDir /r "\\?\$cortexOldDirectory"
    StrCpy $cortexOldMoved ""
  ${EndIf}
!macroend
