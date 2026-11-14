; WinSight installer for Windows 11 / Windows 10 (x64)
; Canonical mainline Inno Setup 6/7 installer script.
; Default installation directory: C:\Program Files\WinSight (64-bit Program Files).
; User data (index, settings, logs) lives under %LOCALAPPDATA%\WinSight\NativePreview\.
; Build via scripts/Build-NativeInstaller.ps1.

#ifndef PayloadDir
  #error Pass /DPayloadDir=<staged app folder>; use scripts/Build-NativeInstaller.ps1.
#endif
#ifndef FilesInclude
  #error Pass /DFilesInclude=<generated [Files] list>; use scripts/Build-NativeInstaller.ps1.
#endif
#ifndef Vendor
  #define Vendor "all"
#endif
#if Vendor == "amd"
  #define VendorLabel "AMD (Ryzen AI)"
  #define SetupBaseName "WinSight-" + AppVersion + "-x64-amd-setup"
#elif Vendor == "intel"
  #define VendorLabel "Intel (OpenVINO)"
  #define SetupBaseName "WinSight-" + AppVersion + "-x64-intel-setup"
#else
  #define VendorLabel "全平台通用 (Shape B)"
  #define SetupBaseName "WinSight-" + AppVersion + "-x64-setup"
#endif
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.1.0.0"
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
OutputBaseFilename={#SetupBaseName}
; ONNX external weights compress to roughly 27-42% with LZMA (sampled), so no entry uses nocompression.
; ultra64 implies a 64 MiB dictionary, which is also the decompression memory needed by Setup.
Compression=lzma2/ultra64
#if Ver < EncodeVer(7, 0, 0)
LZMAUseSeparateProcess=yes
#endif
LZMANumBlockThreads=8
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

; Generated per vendor by Build-NativeInstaller.ps1: [InstallDelete] drops the per-host .NET runtime copies of
; earlier self-contained releases (all hosts now share {app}\runtime), and [Files] lists the common payload
; plus this vendor's models only; identical content is stored once (MergeDuplicateFiles keys on Source).
#include FilesInclude

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

{ Each installer carries one vendor's models only; /IGNORECPUVENDOR bypasses the check for tests. }
function HasCmdLineSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

function InitializeSetup(): Boolean;
begin
#if Vendor == "amd"
  Result := IsAmdCpu();
#elif Vendor == "intel"
  Result := IsIntelCpu();
#else
  // Shape B unified release: supported on all x64 systems; models installed conditionally per CPU vendor.
  Result := True;
#endif
  if not Result and HasCmdLineSwitch('/IGNORECPUVENDOR') then
  begin
    Log('CPU vendor check bypassed by /IGNORECPUVENDOR; detected vendor: ' + GetCpuVendor());
    Result := True;
  end;
  if not Result then
    SuppressibleMsgBox('此安装包仅适用于 {#VendorLabel} 处理器，当前处理器为：' + GetCpuVendor() + #13#10 +
      '请下载与本机 CPU 匹配的 WinSight 安装包。' + #13#10#13#10 +
      'This installer is built for {#VendorLabel} CPUs only. Please download the package that matches this PC.',
      mbCriticalError, MB_OK, IDOK);
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
    DataDir := ExpandConstant('{localappdata}\WinSight\Index');
    { Index, settings and logs belong to the user: preserve on silent uninstalls and ask interactively }
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('是否同时删除 WinSight 的本地索引数据库、配置文件与日志？' + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
