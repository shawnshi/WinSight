[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$EnableCaseSensitiveFixture)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-cne'Desktop'-or$PSVersionTable.PSVersion.Major-ne5-or$PSVersionTable.PSVersion.Minor-ne1-or-not[Environment]::Is64BitProcess){throw 'x64 Windows PowerShell 5.1 is required.'}
$env:PSModulePath=$PSHOME+'\Modules';Import-Module ($PSHOME+'\Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$repo=[IO.Path]::GetFullPath((Join-Path $sourceRoot '..\..'))
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'A fresh owned output directory is required; retain previous failures.'}
for($p=$OutputDirectory;$p;$p=[IO.Path]::GetDirectoryName($p)){if((Test-Path -LiteralPath $p)-and((Get-Item -LiteralPath $p -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Fixture ancestors cannot be reparse points.'}}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$rows=[Collections.Generic.List[object]]::new()
function Check([string]$Group,[string]$Name,[bool]$Pass){$rows.Add([pscustomobject]@{group=$Group;name=$Name;passed=$Pass});if(-not$Pass){throw ('Control failed: '+$Group+'/'+$Name)}}
function Parse([string]$Path){$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors);if(@($errors).Count){throw ('Parser failure: '+$Path)};$ast}
function FunctionBody($Ast,[string]$Name){$found=@($Ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-ceq$Name},$true));if($found.Count-ne1){throw ('Unique source function required: '+$Name)};$found[0].Extent.Text}
$modulePath=Join-Path $sourceRoot 'NativeStandardLiveShell.psm1';$shellAst=Parse $modulePath
$coordinator=Join-Path $sourceRoot 'NativeInstallCoordinator.psm1';$coordinatorAst=Parse $coordinator
foreach($name in @('Assert-NLString','Assert-NLPath','Assert-NLNoReparse','Initialize-NLLinkIdentityApi','Get-NLLinkHandleIdentity','Test-NLStoredLinkFields','Read-NLFreshStoredFields')){. ([scriptblock]::Create((FunctionBody $shellAst $name)))}
foreach($name in @('Get-NativeLiveObservation','Read-NLFreshLink')){Check 'source' ($name+'-uses-actual-predicate') ((FunctionBody $shellAst $name).Contains('Test-NLStoredLinkFields'))}
$worker=FunctionBody $coordinatorAst 'Invoke-N7LiveWorker'
foreach($name in @('Assert-N5Hash','Assert-N5Arguments')){. ([scriptblock]::Create((FunctionBody $coordinatorAst $name)))}
$hash='a'*64
$base=@{Command='ResumeFinalize';Operation='Apply';BindingPath='C:\b.json';BindingSha256=$hash;TransactionId=('b'*32);IntentSha256=$hash;ExpectedReceiptSha256=$hash}
$ok=$true;try{Assert-N5Arguments $base}catch{$ok=$false};Check 'resume' 'resume-finalize-accepted-with-receipt-pin' $ok
$missing=$base.Clone();$null=$missing.Remove('ExpectedReceiptSha256')
$rejected=$false;try{Assert-N5Arguments $missing}catch{$rejected=$true};Check 'resume' 'resume-finalize-rejected-without-receipt-pin' $rejected
$inspect=@{Command='Inspect';Operation='Apply';BindingPath='C:\b.json';BindingSha256=$hash}
$inspectOk=$true;try{Assert-N5Arguments $inspect}catch{$inspectOk=$false};Check 'resume' 'inspect-needs-no-receipt-pin' $inspectOk
$late=$base.Clone();$late.Command='Finalize';$finalOk=$true;try{Assert-N5Arguments $late}catch{$finalOk=$false};Check 'resume' 'finalize-accepted-with-receipt-pin' $finalOk
$complete=FunctionBody $coordinatorAst 'Complete-N7LiveTransaction'
$resume=FunctionBody $coordinatorAst 'Assert-N5Arguments'
Check 'source' 'resume-finalize-remains-reachable' ($resume.Contains("'ResumeFinalize'") -and $complete.Contains('throw'))
Check 'source' 'post-commit-failure-recorded' ($complete.Contains("protocol='StandardPostCommitFailure/1'") -and $complete.Contains("Save-N7Object `$S 'failures' `$failure") -and $complete.Contains('errorType=') -and $complete.Contains('errorMessage='))
Check 'source' 'peer-close-before-disconnect' ($worker.Contains("Wait-N7CallbackPeerClose `$server`n            `$peer.Dispose();`$peer=`$null;`$server.Disconnect()"))
foreach($name in @('Wait-N7CallbackConnection','Wait-N7CallbackPeerClose')){$body=FunctionBody $coordinatorAst $name;Check 'source' ($name+'-preserves-shared-clock') (-not$body.Contains('Initialize-N5Clock')-and-not$body.Contains('$script:N5Start=')-and-not$body.Contains('$script:N5Deadline='))}
$nodes=@{coordinator=$coordinator;entry=(Join-Path $sourceRoot 'Invoke-NativeInstallCoordinator.ps1');live=$modulePath;worker=(Join-Path $sourceRoot 'Invoke-NativeDeployWorker.ps1');launcher=(Join-Path $sourceRoot 'Start-WinSightNative.ps1');builder=(Join-Path $repo 'scripts\Build-StandardNativeInstaller.ps1');core=(Join-Path $sourceRoot 'NativeInstaller.psm1');job=(Join-Path $sourceRoot 'NativeDeploymentJob.psm1')}
foreach($pair in @(@('entry','coordinator'),@('worker','live'),@('builder','live'),@('launcher','coordinator'),@('launcher','entry'),@('launcher','live'),@('launcher','core'),@('launcher','job'))){$hash=(Get-FileHash $nodes[$pair[1]] -Algorithm SHA256).Hash.ToLowerInvariant();Check 'source-pin' ($pair[0]+'-to-'+$pair[1]) ([IO.File]::ReadAllText($nodes[$pair[0]]).Contains("'"+$hash+"'"))}
Check 'source' 'inno673-log-output-api' ([IO.File]::ReadAllText((Join-Path $sourceRoot 'WinSight.Standard.Native.iss')).Contains("CallbackRuntime, SW_HIDE, ewWaitUntilTerminated, Code, nil)"))
# Only the actual pure comparison functions are loaded; the guarded template and live APIs are never invoked.
$out=Join-Path $OutputDirectory 'identity';$null=[IO.Directory]::CreateDirectory($out)
$target=Join-Path $out 'MixedCase.bin';[IO.File]::WriteAllText($target,'OWNED IDENTITY FIXTURE')
$spec=[pscustomobject]@{target=$target;arguments='-NoProfile -File "Owned.ps1"';workingDirectory=$out}
function Fields([string]$Path=$target,[string]$Arg=$spec.arguments,[string]$Dir=$out){[pscustomobject]@{target=$Path;arguments=$Arg;workingDirectory=$Dir}}
Check 'identity' 'exact-contract-preserved' (Test-NLStoredLinkFields (Fields) $spec)
Check 'identity' 'case-only-same-native-file' (Test-NLStoredLinkFields (Fields $target.ToUpperInvariant()) $spec)
Check 'identity' 'lowercase-observed-drive' (Test-NLStoredLinkFields (Fields ($target.Substring(0,1).ToLowerInvariant()+$target.Substring(1))) $spec)
Check 'identity' 'arguments-case-remains-exact' (-not(Test-NLStoredLinkFields (Fields $target '-noprofile -File "Owned.ps1"') $spec))
Check 'identity' 'arguments-whitespace-remains-exact' (-not(Test-NLStoredLinkFields (Fields $target ($spec.arguments+' ')) $spec))
Check 'identity' 'working-directory-remains-exact' (-not(Test-NLStoredLinkFields (Fields $target $spec.arguments $out.ToUpperInvariant()) $spec))
$other=Join-Path $out 'Other.bin';[IO.File]::WriteAllText($other,'OWNED IDENTITY FIXTURE')
Check 'identity' 'same-content-different-path-rejected' (-not(Test-NLStoredLinkFields (Fields $other) $spec))
$a=[IO.File]::OpenRead($target);$b=[IO.File]::OpenRead($other)
try{Check 'identity' 'OS-fileids-distinguish-equal-copies' ((Get-NLLinkHandleIdentity $a)-cne(Get-NLLinkHandleIdentity $b))}finally{$b.Dispose();$a.Dispose()}
$alias=Join-Path $out 'Alias.bin';$null=New-Item -ItemType HardLink -Path $alias -Target $target
$a=[IO.File]::OpenRead($target);$b=[IO.File]::OpenRead($alias)
try{Check 'identity' 'same-fileid-other-hardlink-name-rejected' (((Get-NLLinkHandleIdentity $a)-ceq(Get-NLLinkHandleIdentity $b))-and-not(Test-NLStoredLinkFields (Fields $alias) $spec))}finally{$b.Dispose();$a.Dispose()}
$missing=Join-Path $out 'Missing.bin';$missingSpec=[pscustomobject]@{target=$missing;arguments=$spec.arguments;workingDirectory=$out};$threw=$false
try{$null=Test-NLStoredLinkFields (Fields $missing.ToUpperInvariant()) $missingSpec}catch{$threw=$true};Check 'identity' 'missing-case-variant-fails-closed' $threw
$lock=[IO.File]::Open($target,'Open','ReadWrite','None');$threw=$false
try{try{$null=Test-NLStoredLinkFields (Fields $target.ToUpperInvariant()) $spec}catch{$threw=$true}}finally{$lock.Dispose()};Check 'identity' 'sharing-denial-is-not-success' $threw
$destination=Join-Path $out 'junction-destination';$null=[IO.Directory]::CreateDirectory($destination);[IO.File]::WriteAllText((Join-Path $destination 'app.bin'),'OWNED REPARSE FIXTURE')
$junction=Join-Path $out 'junction';$null=New-Item -ItemType Junction -Path $junction -Target $destination
$reparseSpec=[pscustomobject]@{target=(Join-Path $junction 'app.bin');arguments=$spec.arguments;workingDirectory=$out};$threw=$false
try{$null=Test-NLStoredLinkFields (Fields (Join-Path $junction 'APP.BIN')) $reparseSpec}catch{$threw=$_.Exception.Message-like'*N7.Reparse*'};Check 'identity' 'reparse-ancestor-rejected' $threw
$threw=$false;$closed=[IO.File]::OpenRead($target);$closed.Dispose();try{$null=Get-NLLinkHandleIdentity $closed}catch{$threw=$true};Check 'identity' 'disposed-handle-not-normalized' $threw
$lnkPath=Join-Path $out 'owned.lnk';$com=New-Object -ComObject WScript.Shell;$lnk=$null;$declaredHost=($PSHOME+'\powershell.exe').ToUpperInvariant()
try{$lnk=$com.CreateShortcut($lnkPath);$lnk.TargetPath=$declaredHost;$lnk.Arguments='-NoProfile';$lnk.WorkingDirectory=$out;$lnk.Save()}finally{if($null-ne$lnk){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk)};[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)}
$stored=Read-NLFreshStoredFields $lnkPath;$hostSpec=[pscustomobject]@{target=$declaredHost;arguments='-NoProfile';workingDirectory=$out}
Check 'identity' 'real-WScript-shortcut-fields-accepted' (Test-NLStoredLinkFields $stored $hostSpec)
if($EnableCaseSensitiveFixture){
    $caseDir=Join-Path $out 'case-sensitive';$null=[IO.Directory]::CreateDirectory($caseDir);$saved=$ErrorActionPreference
    try{$ErrorActionPreference='Continue';$text=& ($env:WINDIR+'\System32\fsutil.exe') file setCaseSensitiveInfo $caseDir enable 2>&1;$code=$LASTEXITCODE}finally{$ErrorActionPreference=$saved}
    [IO.File]::WriteAllText((Join-Path $out 'case-sensitive-enable.log'),(($text|ForEach-Object {[string]$_})-join[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
    if($code-ne0){throw ('Requested owned case-sensitive fixture unavailable; exit='+$code)}
    $upper=Join-Path $caseDir 'Case.bin';$lower=Join-Path $caseDir 'case.bin';[IO.File]::WriteAllText($upper,'SAME BYTES DIFFERENT ID');[IO.File]::WriteAllText($lower,'SAME BYTES DIFFERENT ID')
    Check 'identity-case-sensitive' 'two-distinct-case-colliding-files' (@(Get-ChildItem -LiteralPath $caseDir -File).Count-eq2)
    $collision=[pscustomobject]@{target=$upper;arguments=$spec.arguments;workingDirectory=$out}
    Check 'identity-case-sensitive' 'same-content-case-collision-rejected' (-not(Test-NLStoredLinkFields (Fields $lower) $collision))
    Check 'identity-case-sensitive' 'exact-target-still-accepted' (Test-NLStoredLinkFields (Fields $upper) $collision)
    $a=[IO.File]::OpenRead($upper);$b=[IO.File]::OpenRead($lower)
    try{Check 'identity-case-sensitive' 'OS-fileids-really-different' ((Get-NLLinkHandleIdentity $a)-cne(Get-NLLinkHandleIdentity $b))}finally{$b.Dispose();$a.Dispose()}
}
. ([scriptblock]::Create((FunctionBody $coordinatorAst 'Wait-N7CallbackConnection')))
$script:clock=[Diagnostics.Stopwatch]::StartNew();$script:window=1000
function Get-N5Remaining {10000+[Math]::Max(0,$script:window-$script:clock.ElapsedMilliseconds)}
function OwnedExit([int]$Code,[int]$Delay=0){$si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=$PSHOME+'\powershell.exe';$si.Arguments='-NoLogo -NoProfile -NonInteractive -Command "[Threading.Thread]::Sleep('+$Delay+');exit '+$Code+'"';$si.UseShellExecute=$false;$si.CreateNoWindow=$true;[Diagnostics.Process]::Start($si)}
$p=OwnedExit 17
try{
    if(-not$p.WaitForExit(5000)){throw 'Owned nonzero fixture did not settle.'};$never=[Threading.Tasks.TaskCompletionSource[object]]::new();$script:clock.Restart();$message=$null
    try{Wait-N7CallbackConnection $never.Task $p 'Preflight'}catch{$message=$_.Exception.Message}
    Check 'producer' 'nonzero-exit-and-phase-preserved' ($message-like'N7.ProducerExit:*17*Preflight*')
    Check 'producer' 'failed-before-shared-deadline' ((Get-N5Remaining)-gt10000)
    $complete=[Threading.Tasks.TaskCompletionSource[object]]::new();$complete.SetResult($null);$ok=$true;try{Wait-N7CallbackConnection $complete.Task $p 'Checkpoint'}catch{$ok=$false};Check 'producer' 'completed-connect-first' $ok
}finally{if(-not$p.HasExited){$p.Kill();$null=$p.WaitForExit(5000)};$p.Dispose()}
$p=OwnedExit 0
try{if(-not$p.WaitForExit(5000)){throw 'Owned zero fixture did not settle.'};$script:clock.Restart();$ok=$true;try{Wait-N7CallbackConnection ([Threading.Tasks.Task]::Delay(50)) $p 'Checkpoint'}catch{$ok=$false};Check 'producer' 'zero-exit-alone-not-failure' $ok}finally{if(-not$p.HasExited){$p.Kill();$null=$p.WaitForExit(5000)};$p.Dispose()}
$self=[Diagnostics.Process]::GetCurrentProcess()
try{
    $script:window=0;$script:clock.Restart();$never=[Threading.Tasks.TaskCompletionSource[object]]::new();$message=$null;try{Wait-N7CallbackConnection $never.Task $self 'Preflight'}catch{$message=$_.Exception.Message};Check 'producer' 'reserve-exhausted-not-reset' ($message-like'N7.CallbackDeadline:*')
    $fault=[Threading.Tasks.TaskCompletionSource[object]]::new();$fault.SetException([IO.IOException]::new('OWNED_FAULT'));$script:window=1000;$script:clock.Restart();$message=$null;try{Wait-N7CallbackConnection $fault.Task $self 'Preflight'}catch{$message=$_.Exception.Message};Check 'producer' 'faulted-connect-not-swallowed' ($message-like'*OWNED_FAULT*')
}finally{$self.Dispose()}
$p=OwnedExit 17 100
try{$script:window=3000;$script:clock.Restart();$never=[Threading.Tasks.TaskCompletionSource[object]]::new();$message=$null;try{Wait-N7CallbackConnection $never.Task $p 'Checkpoint'}catch{$message=$_.Exception.Message};Check 'producer' 'failure-during-wait-observed' ($message-like'N7.ProducerExit:*17*Checkpoint*'-and(Get-N5Remaining)-gt10000);if(-not$p.WaitForExit(5000)){throw 'Owned delayed fixture not settled.'}}finally{if(-not$p.HasExited){$p.Kill();$null=$p.WaitForExit(5000)};$p.Dispose()}
$cases=@(@('immediate','Immediate',1500,'Normal',20000),@('flush','Flush',1500,'Normal',20000),@('peer-zero','PeerClose',0,'Normal',20000),@('peer-delayed','PeerClose',1500,'Normal',20000),@('peer-slower','PeerClose',2500,'Normal',20000),@('peer-trailing','PeerClose',0,'Trailing',20000),@('peer-closed','PeerClose',0,'CloseBeforeRead',20000),@('peer-stall','PeerClose',0,'Stall',20000))
foreach($case in $cases){
    $saved=$ErrorActionPreference
    try{$ErrorActionPreference='Continue';& ($PSHOME+'\powershell.exe') -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Invoke-OwnedPipeFixture.ps1') -OutputDirectory (Join-Path $OutputDirectory 'pipes') -Case $case[0] -Mode $case[1] -Delay $case[2] -Behavior $case[3] -Budget $case[4] *> (Join-Path $OutputDirectory ($case[0]+'.host.log'));$code=$LASTEXITCODE}finally{$ErrorActionPreference=$saved}
    if($code-ne0){throw ('Owned fixture host failed: '+$case[0]+' exit='+$code)}
    $r=[IO.File]::ReadAllText((Join-Path $OutputDirectory ('pipes\'+$case[0]+'\result.json')))|ConvertFrom-Json
    $settled=$r.ownedProcessAndStreamsSettled-and$r.activeOwnedProcesses-eq0
    if($case[0]-cin@('immediate','flush')){$ok=$settled-and-not$r.client.received-and$r.clientExitCode-eq17-and$r.client.error-like'*N5.Transport: pipe EOF*'}
    elseif($case[0]-ceq'peer-trailing'){$ok=$settled-and$r.serverError-like'*N7.CallbackTrailingData*'}
    elseif($case[0]-ceq'peer-closed'){$ok=$settled-and-not$r.client.received-and$r.clientExitCode-eq33}
    elseif($case[0]-ceq'peer-stall'){$ok=$settled-and$r.serverError-like'*N7.CallbackCloseDeadline*'-and-not$r.client.received-and$r.elapsedMilliseconds-lt20000}
    else{$ok=$settled-and$r.client.received-and$r.clientExitCode-eq0-and$r.peerCloseWaitReturned-and$null-eq$r.serverError}
    Check 'pipe' $case[0] ([bool]$ok)
}
$summary=[pscustomobject]@{passed=$rows.Count;failed=0;groups=@($rows|Group-Object group|ForEach-Object {[pscustomobject]@{name=$_.Name;passed=$_.Count}});controls=$rows;caseSensitiveFixtureRequested=[bool]$EnableCaseSensitiveFixture;realOwnedPipesAndShortcut=$true;productionGuardedModulesNotImported=$true;noNativeOwnerOrNoCommitFabricated=$true;noLiveProgramRegistryMenuWrites=$true;notInstallationOrReleaseAcceptance=$true;fixturesRetained=$true}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),($summary|ConvertTo-Json -Depth 9),[Text.UTF8Encoding]::new($false));$summary|ConvertTo-Json -Depth 9
