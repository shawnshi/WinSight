#ifndef GeneratedConfig
  #error Supply GeneratedConfig from Build-NativeInstaller.ps1
#endif
#include GeneratedConfig
#if CandidateMode == "Production" && FailurePoint != "None"
  #error Production cannot contain failure injection
#endif

[Setup]
AppId=WinSight.Native.{#CandidateMode}
AppName=WinSight Native {#CandidateMode} Candidate
AppVersion=0.0.0
DefaultDirName={#BoundInstallRoot}
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateAppDir=no
PrivilegesRequired=lowest
CreateUninstallRegKey=no
Uninstallable=no
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
AllowCancelDuringInstall=no
SetupLogging=yes
OutputDir={#CandidateOutput}
OutputBaseFilename=WinSight-Native-{#CandidateMode}-Candidate
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
#include PayloadInclude
Source: "{#EngineDirectory}\NativeInstaller.psm1"; DestDir: "{tmp}\winsight-engine"; Flags: ignoreversion
Source: "{#EngineDirectory}\Invoke-NativeInstaller.ps1"; DestDir: "{tmp}\winsight-engine"; Flags: ignoreversion
Source: "{#EngineDirectory}\Invoke-NativeDeployment.ps1"; DestDir: "{tmp}\winsight-engine"; Flags: ignoreversion
Source: "{#EngineDirectory}\Invoke-NativeDeployWorker.ps1"; DestDir: "{tmp}\winsight-engine"; Flags: ignoreversion
Source: "{#EngineDirectory}\NativeDeploymentJob.psm1"; DestDir: "{tmp}\winsight-engine"; Flags: ignoreversion
Source: "{#ReleaseManifest}"; DestDir: "{tmp}\winsight-stage"; Flags: ignoreversion

[Code]
type
  TNativeStartup = record
    Size, Reserved, Desktop, Title, X, Y, XSize, YSize, XChars, YChars, Fill, Flags: LongWord;
    ShowWindow, ReservedSize: Word;
    ReservedData, StdInput, StdOutput, StdError: LongWord;
  end;
  TNativeProcess = record
    ProcessHandle, ThreadHandle, ProcessId, ThreadId: LongWord;
  end;

function NativeCreateProcess(ApplicationName, CommandLine: String; ProcessAttributes, ThreadAttributes: LongWord;
  InheritHandles: Boolean; CreationFlags, Environment, CurrentDirectory: LongWord;
  var Startup: TNativeStartup; var ProcessInfo: TNativeProcess): Boolean;
  external 'CreateProcessW@kernel32.dll stdcall';
function NativeWait(Handle, Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function NativeTerminate(Handle, ExitCode: LongWord): Boolean;
  external 'TerminateProcess@kernel32.dll stdcall';
function NativeExitCode(Handle: LongWord; var ExitCode: LongWord): Boolean;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function NativeClose(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NativeTick: LongWord;
  external 'GetTickCount@kernel32.dll stdcall';

function NativeElapsed(StartTick: LongWord): Int64;
var NowTick: Int64;
begin
  NowTick := NativeTick;
  if NowTick < StartTick then NowTick := NowTick + 4294967296;
  Result := NowTick - StartTick;
end;

function RunBoundedDeployment(const Executable, Params, ControlRoot: String): Boolean;
var
  Startup: TNativeStartup;
  Info: TNativeProcess;
  StartTick, WaitCode, ExitCode: LongWord;
  Remaining: Integer;
  Data: AnsiString;
begin
  { Inno 6.7 setup is 32-bit; the STARTUPINFOW layout is 68 bytes. }
  Startup.Size := 68;
  Startup.Reserved := 0; Startup.Desktop := 0; Startup.Title := 0;
  Startup.X := 0; Startup.Y := 0; Startup.XSize := 0; Startup.YSize := 0;
  Startup.XChars := 0; Startup.YChars := 0; Startup.Fill := 0; Startup.Flags := 0;
  Startup.ShowWindow := 0; Startup.ReservedSize := 0; Startup.ReservedData := 0;
  Startup.StdInput := 0; Startup.StdOutput := 0; Startup.StdError := 0;
  StartTick := NativeTick;
  if not NativeCreateProcess(Executable, '"' + Executable + '" ' + Params +
    ' -StartTick ' + IntToStr(Int64(StartTick)) + ' -DeadlineMilliseconds 140000',
    0, 0, False, $08000000, 0, 0, Startup, Info) then
    RaiseException('Could not create owned deployment dispatcher.');
  NativeClose(Info.ThreadHandle);
  Result := False;
  try
    WaitCode := $00000102;
    while (NativeElapsed(StartTick) < 139000) and (WaitCode = $00000102) do
      WaitCode := NativeWait(Info.ProcessHandle, 25);
    if WaitCode = 0 then
    begin
      if NativeExitCode(Info.ProcessHandle, ExitCode) then Result := ExitCode = 0;
    end
    else
    begin
      { Closing the killed dispatcher's job handle also kills only its assigned worker tree. }
      NativeTerminate(Info.ProcessHandle, 124);
      Remaining := 140000 - NativeElapsed(StartTick);
      if Remaining > 0 then NativeWait(Info.ProcessHandle, Remaining);
      Log('Deployment dispatcher deadline/failure; preserve journal and explicit recovery evidence.');
    end;
  finally
    NativeClose(Info.ProcessHandle);
    if LoadStringFromFile(ControlRoot + '\engine.log', Data) then Log(UTF8Decode(Data));
    if LoadStringFromFile(ControlRoot + '\result.json', Data) then Log(UTF8Decode(Data));
  end;
end;

function IsStateHash(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := Length(Value) = 64;
  if not Result then Exit;
  for I := 1 to Length(Value) do
    if Pos(Value[I], '0123456789abcdef') = 0 then
    begin
      Result := False;
      Exit;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Params, StateHash, ControlRoot: String;
begin
  if CurStep = ssPostInstall then
  begin
    ControlRoot := ExpandConstant('{tmp}\winsight-dispatch');
    Params := '-NoProfile -NonInteractive -File "' + ExpandConstant('{tmp}\winsight-engine\Invoke-NativeDeployment.ps1') +
      '" -EnginePath "' + ExpandConstant('{tmp}\winsight-engine\Invoke-NativeInstaller.ps1') +
      '" -ControlDirectory "' + ControlRoot + '" -StageDirectory "' + ExpandConstant('{tmp}\winsight-stage') +
      '" -InstallRoot "{#BoundInstallRoot}" -DataRoot "{#BoundDataRoot}"' +
      ' -ManifestSha256 {#ManifestHash} -Mode {#CandidateMode}';
#if CandidateMode == "Verification" && FailurePoint != "None"
    Params := Params + ' -VerificationFailurePoint {#FailurePoint}';
#endif
    StateHash := ExpandConstant('{param:STATEHASH|}');
    if StateHash <> '' then
    begin
      if not IsStateHash(StateHash) then RaiseException('Invalid STATEHASH.');
      Params := Params + ' -ExpectedStateSha256 ' + StateHash;
    end;
    if not RunBoundedDeployment(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, ControlRoot) then
      RaiseException('Deployment failed. Preserve the transaction journal and setup log; use explicit hash-bound recovery.');
  end;
end;
