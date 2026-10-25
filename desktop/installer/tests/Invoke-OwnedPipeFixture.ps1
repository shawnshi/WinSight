[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('Server','Client')][string]$Role='Server',
    [ValidateSet('Immediate','Flush','PeerClose')][string]$Mode='PeerClose',
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-z0-9-]+$')][string]$Case,
    [string]$PipeName,
    [ValidateRange(0,5000)][int]$Delay=100,
    [ValidateSet('Normal','CloseBeforeRead','Trailing','Stall')][string]$Behavior='Normal',
    [ValidateRange(1000,20000)][int]$Budget=20000
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-cne'Desktop'-or$PSVersionTable.PSVersion.Major-ne5-or$PSVersionTable.PSVersion.Minor-ne1-or-not[Environment]::Is64BitProcess){throw 'Owned fixture requires x64 Windows PowerShell 5.1.'}
foreach($name in [Environment]::GetEnvironmentVariables().Keys){if($name-clike'N5_*'-or$name-clike'N7_*'){throw 'Native capabilities are forbidden in the synthetic fixture.'}}
$env:PSModulePath=$PSHOME+'\Modules'
Import-Module ($PSHOME+'\Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $sourceRoot 'NativeDeploymentJob.psm1') -Force
$modulePath=Join-Path $sourceRoot 'NativeInstallCoordinator.psm1'
$sourceHash=(Get-FileHash $modulePath -Algorithm SHA256).Hash.ToLowerInvariant()
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($modulePath,[ref]$tokens,[ref]$errors)
if(@($errors).Count){throw 'Actual transport source parse failed.'}
foreach($name in @('ConvertTo-N5Bytes','ConvertFrom-N5Bytes','Get-N5Remaining','Read-N5Pipe','Send-N5Pipe','Receive-N5Pipe','Wait-N7CallbackPeerClose')){
    $found=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-ceq$name},$true))
    if($found.Count-ne1){throw ('Unique actual function required: '+$name)}
    . ([scriptblock]::Create($found[0].Extent.Text))
}
$script:N5Utf8=[Text.UTF8Encoding]::new($false,$true);$script:N5Limit=2097152
$script:N5Start=Get-NativeTick;$script:N5Deadline=$Budget
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory);$out=Join-Path $OutputDirectory $Case
if($Role-ceq'Client'){
    if($PipeName-cnotmatch'^WinSightOwnedPipeTest-[0-9a-f]{32}$'){throw 'Only the owned synthetic pipe is accepted.'}
    $pipe=$null;$received=$false;$errorText=$null;$code=0
    try{
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,'InOut','Asynchronous');$pipe.Connect(8000)
        Send-N5Pipe $pipe ([pscustomobject]@{protocol='OwnedSyntheticPipeProbe/1';purpose='transport-only-not-authority';sequence=1})
        if($Behavior-ceq'CloseBeforeRead'){$code=33}else{
            if($Behavior-ceq'Stall'){Start-Sleep -Milliseconds 12000}else{Start-Sleep -Milliseconds $Delay}
            $reply=Receive-N5Pipe $pipe
            if($reply.protocol-cne'OwnedSyntheticPipeReply/1'-or$reply.payload-cne('X'*200)){throw 'Synthetic reply mismatch.'}
            $received=$true
            if($Behavior-ceq'Trailing'){$extra=[byte[]]@(81);$pipe.Write($extra,0,1)}
        }
    }catch{$errorText=$_.Exception.Message;$code=17}finally{if($null-ne$pipe){$pipe.Dispose()}}
    $result=[pscustomobject]@{role='Client';pid=$PID;received=$received;error=$errorText;exitCode=$code;behavior=$Behavior;syntheticOnly=$true;nativeAuthorityUsed=$false}
    [IO.File]::WriteAllText((Join-Path $out 'client-result.json'),($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false));exit $code
}
if(Test-Path -LiteralPath $out){throw 'Fresh owned case directory required.'}
$null=[IO.Directory]::CreateDirectory((Join-Path $out 'tmp'))
$pipeName='WinSightOwnedPipeTest-'+[Guid]::NewGuid().ToString('N')
$server=$null;$client=$null;$job=[IntPtr]::Zero;$stdoutTask=$null;$stderrTask=$null;$serverError=$null;$requestSeen=$false;$replyWritten=$false;$waitReturned=$false;$childCode=$null;$active=$null;$settled=$false
$elapsed=[Diagnostics.Stopwatch]::StartNew()
try{
    # Match the production buffered constructor; a zero-buffer pipe hides the original lost-reply race.
    $server=[IO.Pipes.NamedPipeServerStream]::new($pipeName,'InOut',1,'Byte','Asynchronous',65536,65536)
    $connect=$server.WaitForConnectionAsync()
    $si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=$PSHOME+'\powershell.exe'
    $si.Arguments='-NoLogo -NoProfile -NonInteractive -File "'+$PSCommandPath+'" -OutputDirectory "'+$OutputDirectory+'" -Role Client -Mode '+$Mode+' -Case '+$Case+' -PipeName '+$pipeName+' -Delay '+$Delay+' -Behavior '+$Behavior+' -Budget 15000'
    $si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $si.EnvironmentVariables['TEMP']=Join-Path $out 'tmp';$si.EnvironmentVariables['TMP']=Join-Path $out 'tmp'
    $job=New-NativeDeploymentJob;$client=[Diagnostics.Process]::new();$client.StartInfo=$si;$null=$client.Start()
    if(-not[WinSight.DeploymentJobApi]::AssignProcessToJobObject($job,$client.Handle)){throw 'Owned client Job assignment failed.'}
    $stdoutTask=$client.StandardOutput.ReadToEndAsync();$stderrTask=$client.StandardError.ReadToEndAsync()
    if(-not$connect.Wait(8000)){throw 'Owned client connect timeout.'};$null=$connect.GetAwaiter().GetResult()
    $request=Receive-N5Pipe $server
    if($request.protocol-cne'OwnedSyntheticPipeProbe/1'){throw 'Unexpected synthetic request.'};$requestSeen=$true
    Send-N5Pipe $server ([pscustomobject]@{protocol='OwnedSyntheticPipeReply/1';payload=('X'*200)});$replyWritten=$true
    if($Mode-ceq'Flush'){$server.Flush()}
    if($Mode-ceq'PeerClose'){Wait-N7CallbackPeerClose $server;$waitReturned=$true}
    $server.Disconnect()
}catch{$serverError=$_.Exception.Message}finally{
    if($null-ne$server){$server.Dispose()}
    if($null-ne$client){if(-not$client.WaitForExit(8000)){if($job-ne[IntPtr]::Zero){$null=[WinSight.DeploymentJobApi]::TerminateJobObject($job,124)};if(-not$client.WaitForExit(3000)){throw 'Owned client did not settle.'}};$childCode=$client.ExitCode}
    if($null-ne$stdoutTask-and$null-ne$stderrTask){
        if(-not$stdoutTask.Wait(1000)-or-not$stderrTask.Wait(1000)){throw 'Owned client streams not settled.'}
        [IO.File]::WriteAllText((Join-Path $out 'client.stdout.log'),$stdoutTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $out 'client.stderr.log'),$stderrTask.GetAwaiter().GetResult())
    }
    if($job-ne[IntPtr]::Zero){$active=Get-NativeJobActiveProcesses $job;$null=[WinSight.DeploymentJobApi]::CloseHandle($job)}
    if($null-ne$client){$client.Dispose()};$settled=($active-eq0-and$null-ne$childCode)
}
$clientResult=$null;$clientPath=Join-Path $out 'client-result.json'
if(Test-Path -LiteralPath $clientPath){$clientResult=[IO.File]::ReadAllText($clientPath)|ConvertFrom-Json}
$result=[pscustomobject]@{mode=$Mode;sourceSha256=$sourceHash;case=$Case;behavior=$Behavior;serverError=$serverError;requestSeen=$requestSeen;replyWritten=$replyWritten;peerCloseWaitReturned=$waitReturned;client=$clientResult;clientExitCode=$childCode;activeOwnedProcesses=$active;ownedProcessAndStreamsSettled=$settled;elapsedMilliseconds=$elapsed.ElapsedMilliseconds;nativeInstallerOrAuthorityUsed=$false;notBirthIsolationOrSandbox=$true}
[IO.File]::WriteAllText((Join-Path $out 'result.json'),($result|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
if(-not$settled){exit 1}
