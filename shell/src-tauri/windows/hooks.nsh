; WinSight's additions to Tauri's NSIS installer (bundle.windows.nsis.installerHooks).

; The helper folder is replaced whole: an update would otherwise leave behind what an older version had there and
; this one doesn't (another ffmpeg's DLLs).
!macro NSIS_HOOK_PREINSTALL
  ; Framework-dependent Windows ML host: do not silently install a global runtime.
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  SetRegView 64
  StrCpy $4 "x64"
  ReadEnvStr $3 PROCESSOR_ARCHITEW6432
  ${If} $3 == "ARM64"
    StrCpy $4 "arm64"
  ${Else}
    ReadEnvStr $3 PROCESSOR_ARCHITECTURE
    ${If} $3 == "ARM64"
      StrCpy $4 "arm64"
    ${EndIf}
  ${EndIf}
  StrCpy $0 0
  winsight_dotnet_check:
    EnumRegValue $1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\$4\sharedfx\Microsoft.NETCore.App" $0
    ${If} $1 == ""
      MessageBox MB_OK|MB_ICONSTOP "WinSight requires Microsoft .NET 10 $4 Runtime for its native inference host. Install it before installing WinSight." /SD IDOK
      Abort
    ${EndIf}
    StrCpy $2 $1 3
    ${If} $2 == "10."
      Goto winsight_dotnet_ready
    ${EndIf}
    IntOp $0 $0 + 1
    Goto winsight_dotnet_check
  winsight_dotnet_ready:
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  ${If} $INSTDIR != ""
    RMDir /r "$INSTDIR\helper"
  ${EndIf}
!macroend

; "Delete the application data" also takes WinSight's ONNX model files (about 505 MB) and its index: the app keeps them in
; %LOCALAPPDATA%\WinSight, not in the data folders named after its identifier, which Tauri deletes. Never on an update.
!macro NSIS_HOOK_POSTUNINSTALL
  ${If} $DeleteAppDataCheckboxState = 1
  ${AndIf} $UpdateMode <> 1
    RMDir /r "$LOCALAPPDATA\WinSight\Models"
    RMDir /r "$LOCALAPPDATA\WinSight\index"
    RMDir "$LOCALAPPDATA\WinSight"
  ${EndIf}
!macroend
