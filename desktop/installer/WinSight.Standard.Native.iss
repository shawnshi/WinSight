#ifndef GeneratedConfig
  #error Supply GeneratedConfig from Build-StandardNativeInstaller.ps1
#endif
#include GeneratedConfig

[Setup]
AppId=WinSight.Native.Preview
AppName=WinSight Native Preview
AppVersion=0.0.0
AppPublisher=WinSight
UninstallDisplayName=WinSight Native Preview
DefaultDirName={#BoundInstallRoot}
UninstallFilesDir={app}
DefaultGroupName=WinSight Native Preview
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
UsePreviousTasks=no
CreateUninstallRegKey=yes
Uninstallable=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
AllowCancelDuringInstall=no
SetupLogging=yes
OutputDir={#CandidateOutput}
OutputBaseFilename=WinSight-Standard-Native-Candidate
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#LauncherSource}"; DestDir: "{app}\shell"; DestName: "Start-WinSightNative.ps1"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\WinSight Native Preview\WinSight Native Preview"; Filename: "{#BoundHost}"; Parameters: "{code:LaunchArguments}"; WorkingDir: "{app}\shell"
Name: "{userprograms}\WinSight Native Preview\Uninstall WinSight Native Preview"; Filename: "{#BoundHost}"; Parameters: "{code:UninstallArguments}"; WorkingDir: "{#RuntimeRoot}"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1';
var
  CallbackFailed: Boolean;
  CallbackRuntime: String;

function IsHash(const Value: String): Boolean;
var I: Integer;
begin
  Result := Length(Value) = 64;
  if not Result then Exit;
  for I := 1 to Length(Value) do
    if Pos(Value[I], '0123456789abcdef') = 0 then begin Result := False; Exit; end;
end;

procedure RequireTrustedRuntime(const Runtime: String);
begin
  { Environment pins are claims; only independently compiled source pins authorize callback code. }
  if (GetEnv('N7_ENTRY_HASH') <> '{#SourceHash4}') or
     (GetEnv('N7_MODULE_HASH') <> '{#SourceHash3}') or
     (GetSHA256OfFile('{#BoundHost}') <> '{#BoundHostHash}') or
     (GetSHA256OfFile(Runtime + '\NativeInstaller.psm1') <> '{#SourceHash0}') or
     (GetSHA256OfFile(Runtime + '\Invoke-NativeInstaller.ps1') <> '{#SourceHash1}') or
     (GetSHA256OfFile(Runtime + '\NativeDeploymentJob.psm1') <> '{#SourceHash2}') or
     (GetSHA256OfFile(Runtime + '\NativeInstallCoordinator.psm1') <> '{#SourceHash3}') or
     (GetSHA256OfFile(Runtime + '\Invoke-NativeInstallCoordinator.ps1') <> '{#SourceHash4}') or
     (GetSHA256OfFile(Runtime + '\NativeStandardLiveShell.psm1') <> '{#SourceHash5}') or
     (GetSHA256OfFile(Runtime + '\Invoke-NativeDeployment.ps1') <> '{#SourceHash6}') or
     (GetSHA256OfFile(Runtime + '\Invoke-NativeDeployWorker.ps1') <> '{#SourceHash7}') or
     (GetSHA256OfFile(Runtime + '\Start-WinSightNative.ps1') <> '{#SourceHash8}') then
    RaiseException('B.SourceCompatibilityRequired: callback code differs from compiled trusted source9.');
end;

procedure RequireOwner(const Operation: String);
var BindingHash, BindingPath, Entry, Prefix, Suffix, Digest: String;
begin
  BindingHash := GetEnv('N7_LIVE_BINDING');
  BindingPath := GetEnv('N7_LIVE_BINDING_PATH');
  Entry := GetEnv('N7_LIVE_ENTRY');
  Prefix := '{#BoundControlRoot}\runtime\';
  Suffix := '\Invoke-NativeInstallCoordinator.ps1';
  Digest := Copy(Entry, Length(Prefix) + 1, 64);
  CallbackRuntime := Prefix + Digest;
  if (not IsHash(Digest)) or (Entry <> CallbackRuntime + Suffix) or
     ((Operation = 'Apply') and (CallbackRuntime <> '{#RuntimeRoot}')) then
    RaiseException('B.OwnerContext: exact canonical source runtime required.');
  if (GetEnv('N7_LIVE_HOST') <> '{#BoundHost}') or
     (GetEnv('N7_LIVE_TASK_ROOT') <> '{#BoundTaskRoot}') or
     (GetEnv('N7_LIVE_OPERATION') <> Operation) or
     (not IsHash(BindingHash)) or (not IsHash(GetEnv('N7_LIVE_INTENT'))) or
     (not IsHash(GetEnv('N7_MODULE_HASH'))) or (not IsHash(GetEnv('N7_ENTRY_HASH'))) or
     (Length(GetEnv('N7_LIVE_TRANSACTION')) <> 32) or
     (Length(GetEnv('N7_LIVE_NONCE')) <> 64) or
     (Pos('WinSightN7-', GetEnv('N7_OWNER_PIPE')) <> 1) or
     (GetEnv('N7_START') = '') or (GetEnv('N7_DEADLINE') = '') or
     (BindingPath <> '{#BoundControlRoot}\bindings\' + BindingHash + '.json') then
    RaiseException('B.OwnerContext: genuine inherited owner context required.');
  if (GetSHA256OfFile(Entry) <> GetEnv('N7_ENTRY_HASH')) or
     (GetSHA256OfFile(BindingPath) <> BindingHash) then
    RaiseException('B.OwnerContext: inherited entry/binding bytes changed.');
  RequireTrustedRuntime(CallbackRuntime);
end;

procedure Callback(const Operation, Phase: String);
var Code: Integer;
begin
  CallbackFailed := True;
  RequireOwner(Operation);
  { A owns the original deadline and inherited Job; Exec neither elevates nor commits. }
  if not Exec('{#BoundHost}', '-NoProfile -NonInteractive -File "' +
    GetEnv('N7_LIVE_ENTRY') + '" -LiveCallback ' + Phase,
    CallbackRuntime, SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException('B.CallbackStart: owned callback could not start.');
  if Code <> 0 then RaiseException('B.CallbackRejected: NoCommit acknowledgment failed: ' + IntToStr(Code));
  CallbackFailed := False;
end;

function BindingArguments: String;
begin
  Result := ' -BindingPath "' + GetEnv('N7_LIVE_BINDING_PATH') +
    '" -BindingSha256 ' + GetEnv('N7_LIVE_BINDING');
end;

function LaunchArguments(Param: String): String;
begin
  Result := '-NoProfile -NonInteractive -File "{#BoundInstallRoot}\shell\Start-WinSightNative.ps1"' + BindingArguments;
end;

function UninstallArguments(Param: String): String;
begin
  Result := '-NoProfile -NonInteractive -File "{#RuntimeRoot}\Invoke-NativeDeployment.ps1" -Action Uninstall' + BindingArguments;
end;

procedure RegisterOuterController;
var Genuine, Location: String;
begin
  { Setup.Install.pas:309 creates the genuine key first. Never create a key for Pending. }
  if not RegKeyExists(HKCU64, UninstallKey) then RaiseException('B.Registration: genuine registration missing.');
  if not RegQueryStringValue(HKCU64, UninstallKey, 'UninstallString', Genuine) or
     (Genuine <> '"{#BoundInstallRoot}\unins000.exe"') then RaiseException('B.Registration: genuine uninstaller mismatch.');
  if not RegQueryStringValue(HKCU64, UninstallKey, 'InstallLocation', Location) or
     (Location <> '{#BoundInstallRoot}\') then RaiseException('B.Registration: genuine install location mismatch.');
  if not RegWriteStringValue(HKCU64, UninstallKey, 'InstallLocation', '{#BoundInstallRoot}') or
     not RegWriteStringValue(HKCU64, UninstallKey, 'UninstallString', '"{#BoundHost}" ' + UninstallArguments('')) or
     not RegWriteStringValue(HKCU64, UninstallKey, 'QuietUninstallString', '"{#BoundHost}" ' + UninstallArguments('') + ' -Quiet') then
    RaiseException('B.Registration: outer controller registration failed.');
end;

function InitializeSetup: Boolean;
begin
  Callback('Apply', 'Preflight');
  Result := True;
end;

function InitializeUninstall: Boolean;
begin
  Callback('Remove', 'Preflight');
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    CallbackFailed := True;
    if ExpandConstant('{app}') <> '{#BoundInstallRoot}' then RaiseException('B.Root: exact /DIR required.');
    RegisterOuterController;
    { ssPostInstall is outside Inno rollback. A observes final bytes only after settlement. }
    Callback('Apply', 'Checkpoint');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then Callback('Remove', 'Checkpoint');
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if CallbackFailed then Result := 1;
end;
