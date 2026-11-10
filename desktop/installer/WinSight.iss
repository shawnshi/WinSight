; WinSight installer for Windows 11 / Windows 10 (x64)
; Canonical mainline Inno Setup 6 installer script.
; Default installation directory: C:\Program Files\WinSight (64-bit Program Files).
; User data (index, settings, logs) lives under %LOCALAPPDATA%\WinSight\NativePreview\.
; Build via scripts/Build-NativeInstaller.ps1.

#ifndef PayloadDir
  #error Pass /DPayloadDir=<staged app folder>; use scripts/Build-NativeInstaller.ps1.
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.0.0.0"
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
Compression=lzma2/normal
LZMAUseSeparateProcess=yes
LZMADictionarySize=32768
SolidCompression=yes
DiskSpanning=yes
DiskSliceSize=2100000000
WizardStyle=modern
SetupLogging=yes

; Graceful process termination across active WinSight execution roles
CloseApplications=yes
CloseApplicationsFilter=WinSight.App.exe,winsight-native.exe,winsight-mcp.exe,WinSight.InferenceHost.exe,WinSight.OcrHost.exe,WinSight.WinMlHost.exe
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
Source: "{#PayloadDir}\*"; Excludes: "models\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 通用模型配置与基础数据文件 (全平台安装)
Source: "{#PayloadDir}\models\*"; Excludes: "RyzenAI\*,IntelOpenVINO\*"; DestDir: "{app}\models"; Flags: ignoreversion recursesubdirs createallsubdirs
; AMD 平台专用高熵大权重与微码 (流式归档不耗时压缩)
Source: "{#PayloadDir}\models\RyzenAI\*.onnx_data"; DestDir: "{app}\models\RyzenAI"; Check: IsAmdCpu; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist nocompression
Source: "{#PayloadDir}\models\RyzenAI\*.rai"; DestDir: "{app}\models\RyzenAI"; Check: IsAmdCpu; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist nocompression
Source: "{#PayloadDir}\models\RyzenAI\*"; Excludes: "*.onnx_data,*.rai"; DestDir: "{app}\models\RyzenAI"; Check: IsAmdCpu; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Intel 平台专用高熵大权重 (流式归档不耗时压缩)
Source: "{#PayloadDir}\models\IntelOpenVINO\*.onnx_data"; DestDir: "{app}\models\IntelOpenVINO"; Check: IsIntelCpu; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist nocompression
Source: "{#PayloadDir}\models\IntelOpenVINO\*"; Excludes: "*.onnx_data"; DestDir: "{app}\models\IntelOpenVINO"; Check: IsIntelCpu; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\WinSight"; Filename: "{app}\WinSight.App.exe"
Name: "{autodesktop}\WinSight"; Filename: "{app}\WinSight.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WinSight.App.exe"; Description: "启动 WinSight (Start WinSight)"; Flags: nowait postinstall skipifsilent runascurrentuser

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'WinSight';
  UserEnvKey = 'Environment';
  SystemEnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function GetCpuVendor(): String;
var
  Vendor: String;
begin
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'VendorIdentifier', Vendor) then
    Result := Uppercase(Vendor)
  else
    Result := '';
end;

function IsAmdCpu(): Boolean;
begin
  Result := Pos('AMD', GetCpuVendor()) > 0;
end;

function IsIntelCpu(): Boolean;
begin
  Result := Pos('INTEL', GetCpuVendor()) > 0;
end;

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
