; WinSight installer for Windows 11 / Windows 10 (x64)
; Canonical mainline Inno Setup 6 installer script.
; Default installation directory: C:\Program Files\WinSight (64-bit Program Files).
; User data (index, settings, logs) lives under %LOCALAPPDATA%\WinSight\NativePreview\.
; Build via scripts/Build-NativeInstaller.ps1.

#ifndef PayloadDir
  #error Pass /DPayloadDir=<staged app folder>; use scripts/Build-NativeInstaller.ps1.
#endif
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.3.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{3F8B2A64-7C1D-4E5A-9B0E-5D2C8A1F6E77}
AppName=WinSight
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=WinSight
DefaultDirName={autopf64}\WinSight
DefaultGroupName=WinSight
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\Resources\AppIcon\WinSight.ico
UninstallDisplayName=WinSight
UninstallDisplayIcon={app}\WinSight.App.exe
OutputDir={#OutputDir}
OutputBaseFilename=WinSight-{#AppVersion}-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

; Graceful process termination across all 7 WinSight execution roles
CloseApplications=yes
CloseApplicationsFilter=WinSight.App.exe,winsight-native.exe,winsight-mcp.exe,WinSight.InferenceHost.exe,WinSight.OcrHost.exe,WinSight.WinMlHost.exe,WinSight.FoundryHost.exe
RestartApplications=no

#ifdef ExtraDiskSpace
ExtraDiskSpaceRequired={#ExtraDiskSpace}
#endif

#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式 (&Desktop shortcut)"; Flags: unchecked
Name: "addtopath"; Description: "将 WinSight 工具添加至系统 PATH 环境变量 (&Add to PATH)"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinSight"; Filename: "{app}\WinSight.App.exe"
Name: "{autodesktop}\WinSight"; Filename: "{app}\WinSight.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WinSight.App.exe"; Description: "启动 WinSight (Start WinSight)"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'WinSight';
  UserEnvKey = 'Environment';
  SystemEnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

procedure AddToPath();
var
  AppDir, OldPath, NewPath, TargetKey: String;
  RootKey: Integer;
begin
  AppDir := ExpandConstant('{app}');
  if IsAdminInstallMode then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    TargetKey := SystemEnvKey;
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    TargetKey := UserEnvKey;
  end;

  if RegQueryStringValue(RootKey, TargetKey, 'Path', OldPath) then
  begin
    if Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OldPath) + ';') = 0 then
    begin
      if (Length(OldPath) > 0) and (OldPath[Length(OldPath)] <> ';') then
        NewPath := OldPath + ';' + AppDir
      else
        NewPath := OldPath + AppDir;
      RegWriteStringValue(RootKey, TargetKey, 'Path', NewPath);
    end;
  end
  else
    RegWriteStringValue(RootKey, TargetKey, 'Path', AppDir);
end;

procedure RemoveFromPath();
var
  AppDir, CurrentPath, P, TargetKey: String;
  RootKey, StartPos: Integer;
begin
  AppDir := ExpandConstant('{app}');
  if IsAdminInstallMode then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    TargetKey := SystemEnvKey;
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    TargetKey := UserEnvKey;
  end;

  if RegQueryStringValue(RootKey, TargetKey, 'Path', CurrentPath) then
  begin
    P := ';' + CurrentPath + ';';
    StartPos := Pos(';' + Uppercase(AppDir) + ';', Uppercase(P));
    if StartPos > 0 then
    begin
      Delete(P, StartPos, Length(AppDir) + 1);
      if (Length(P) > 0) and (P[1] = ';') then Delete(P, 1, 1);
      if (Length(P) > 0) and (P[Length(P)] = ';') then Delete(P, Length(P), 1);
      RegWriteStringValue(RootKey, TargetKey, 'Path', P);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
      AddToPath();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Purge login run item if registered }
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunValue);
    { Cleanly remove from PATH }
    RemoveFromPath();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\WinSight\NativePreview');
    { Index, settings and logs belong to the user: preserve on silent uninstalls and ask interactively }
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('是否同时删除 WinSight 的本地索引数据库、配置文件与日志？' + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
