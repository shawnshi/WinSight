[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot,[ValidateSet('success','hang64','pipe','fail','hang32')][string[]]$Cases=@('success','hang64','pipe','fail','hang32'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$engine=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $engine 'NativeInstaller.psm1') -Force
Import-Module (Join-Path $engine 'NativeDeploymentJob.psm1') -Force
$root=Resolve-NativeRoot $TaskRoot
if(Test-Path -LiteralPath $root){throw 'Test root exists'}
[IO.Directory]::CreateDirectory($root)|Out-Null
Start-Transcript -LiteralPath (Join-Path $root 'test.log') -NoClobber|Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Name){if(-not $Value){throw "ASSERTION FAILED: $Name"};$checks.Add($Name);Write-Output "PASS $Name"}
$fixture=Join-Path $root 'engine.ps1'
$child=Join-Path $root 'pipe.ps1'
$module=Join-Path $engine 'NativeInstaller.psm1'
$fixtureText=@'
param($Operation,$InstallRoot,$DataRoot,$StageDirectory,$ManifestSha256,$Mode,$VerificationFailurePoint)
$ErrorActionPreference='Stop'
Import-Module '__MODULE__' -Force
$name=Split-Path $InstallRoot -Leaf
$hook={param($point)
 if($point -eq 'AfterJournal'){
  [IO.File]::WriteAllText('__ROOT__/'+$name+'.worker.pid',$PID.ToString())
  if($name.StartsWith('hang')){while($true){Start-Sleep -Seconds 1}}
  if($name -eq 'pipe'){
   $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
   $start.Arguments='-NoProfile -NonInteractive -File "__CHILD__"';$start.UseShellExecute=$false
   $p=[Diagnostics.Process]::Start($start);try{[IO.File]::WriteAllText('__ROOT__/pipe.child.pid',$p.Id.ToString())}finally{$p.Dispose()}
   exit 0
  }
  if($name -eq 'fail'){throw 'fixture dependency failure'}
 }
}.GetNewClosure()
Invoke-NativeTransaction -Operation Apply -InstallRoot $InstallRoot -DataRoot $DataRoot -StageDirectory $StageDirectory -ManifestSha256 $ManifestSha256 -Mode $Mode -TestHook $hook
'@
$fixtureText=$fixtureText.Replace('__MODULE__',$module).Replace('__ROOT__',$root).Replace('__CHILD__',$child)
[IO.File]::WriteAllText($fixture,$fixtureText)
[IO.File]::WriteAllText($child,'while($true){Start-Sleep -Seconds 1}')
$stage=Join-Path $root 'stage';[IO.Directory]::CreateDirectory((Join-Path $stage 'app'))|Out-Null
[IO.File]::WriteAllText((Join-Path $stage 'app/fixture.bin'),'inert test data')
$item=Get-Item -LiteralPath (Join-Path $stage 'app/fixture.bin')
[pscustomobject]@{schema=1;runtime='win-x64';selfContained=$true;appOnlyPreview=$true;releaseApproved=$false;files=@([pscustomobject]@{path='app/fixture.bin';bytes=$item.Length;sha256=Get-NativeHash $item.FullName})}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
$hash=Get-NativeHash (Join-Path $stage 'release-manifest.json')
$data=Join-Path $root 'data';[IO.Directory]::CreateDirectory($data)|Out-Null
[IO.File]::WriteAllText((Join-Path $data 'sentinel'),'data remains untouched')
$dataHash=Get-NativeHash (Join-Path $data 'sentinel')
$results=[Collections.Generic.List[object]]::new()
try{
 foreach($name in $Cases){
  $testDeadline=4000;if($name -eq 'hang32'){$testDeadline=10000}
  $exe=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
  if($name -eq 'hang32'){$exe=Join-Path $env:SystemRoot 'SysWOW64/WindowsPowerShell/v1.0/powershell.exe'}
  $control=Join-Path $root ($name+'-control');$install=Join-Path $root $name
  $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$exe
  $start.Arguments='-NoProfile -NonInteractive -File "'+(Join-Path $engine 'Invoke-NativeDeployment.ps1')+'" -EnginePath "'+$fixture+'" -ControlDirectory "'+$control+'" -InstallRoot "'+$install+'" -DataRoot "'+$data+'" -StageDirectory "'+$stage+'" -ManifestSha256 '+$hash+' -DeadlineMilliseconds '+$testDeadline+' -StartTick '+(Get-NativeTick)
  $start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $p=[Diagnostics.Process]::new();$p.StartInfo=$start
  try{
   Check ($p.Start()) ('dispatcher starts '+$name)
   $out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
   if(-not $p.WaitForExit(12000)){$p.Kill();$null=$p.WaitForExit(2000);throw 'Owned dispatcher test exceeded hard fixture limit'}
   Check ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out,$err),1000)) ('outer fixture pipes settle '+$name)
   [IO.File]::WriteAllText((Join-Path $root ($name+'.log')),$out.Result+$err.Result)
   $code=$p.ExitCode
  }finally{$p.Dispose()}
  $receipt=Get-Content -LiteralPath (Join-Path $control 'result.json') -Raw|ConvertFrom-Json
  $results.Add($receipt)
  Check ($receipt.processSettled -and $receipt.activeOwnedProcesses -eq 0 -and $receipt.readersSettled -and $receipt.drained) ('job worker, descendants and readers settle '+$name)
  Check ($receipt.elapsedMilliseconds -lt ($testDeadline+1000)) ('one shared lower-only deadline '+$name)
  if($name -eq 'success'){
   Check ($code -eq 0 -and -not $receipt.timedOut) 'natural real engine completion, no success injection'
   Check (-not(Test-Path -LiteralPath (Join-Path $install 'journal.json'))) 'completed engine commits normally'
  }else{
   Check ($code -ne 0) ('failure is not swallowed '+$name)
   Check (Test-Path -LiteralPath (Join-Path $install 'journal.json')) ('actual recovery journal retained '+$name)
   if($name -ne 'fail'){Check $receipt.timedOut ('hanging or pipe-holding owned job is terminated '+$name)}
   foreach($pidFile in @(Get-ChildItem -LiteralPath $root -Filter ($name+'*.pid') -File)){
    $owned=$null
    try{$owned=[Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($pidFile.FullName));Check $owned.HasExited ('owned PID settled '+$pidFile.Name)}
    catch [ArgumentException]{Check $true ('owned PID gone '+$pidFile.Name)}
    finally{if($null -ne $owned){$owned.Dispose()}}
   }
  }
 }
 Check ((Get-NativeHash (Join-Path $data 'sentinel')) -ceq $dataHash) 'data sentinel unchanged across dispatcher failures'
 [pscustomobject]@{passed=$true;checks=@($checks);results=@($results);sourceHashes=@{dispatcher=Get-NativeHash (Join-Path $engine 'Invoke-NativeDeployment.ps1');worker=Get-NativeHash (Join-Path $engine 'Invoke-NativeDeployWorker.ps1');job=Get-NativeHash (Join-Path $engine 'NativeDeploymentJob.psm1')};actualSetupExecuted=$false}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $root 'summary.json') -Encoding utf8
 Write-Output "PASS $($checks.Count) dispatcher regressions; no setup."
}finally{Stop-Transcript|Out-Null}
