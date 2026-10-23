[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot,[Parameter(Mandatory)][string]$ModulePath,[switch]$Before)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module $ModulePath -Force
$root=Resolve-NativeRoot $TaskRoot
if(Test-Path -LiteralPath $root){throw 'Test root exists'}
[IO.Directory]::CreateDirectory($root)|Out-Null
Start-Transcript -LiteralPath (Join-Path $root 'test.log') -NoClobber|Out-Null
$checks=[Collections.Generic.List[string]]::new()
$observed=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Name){if(-not $Value){throw "ASSERTION FAILED: $Name"};$checks.Add($Name);Write-Output "PASS $Name"}
function Reject([scriptblock]$Body,[string]$Name){$caught=$null;try{&$Body|Out-Null}catch{$caught=$_};Check ($null -ne $caught) $Name}
function MakeStage([string]$Name,[byte]$Value){
 $stage=Join-Path $root $Name;[IO.Directory]::CreateDirectory((Join-Path $stage 'app'))|Out-Null
 $bytes=New-Object byte[] 196608;for($i=0;$i -lt $bytes.Length;$i++){$bytes[$i]=$Value}
 [IO.File]::WriteAllBytes((Join-Path $stage 'app/WinSight.App.exe'),$bytes)
 $files=@([pscustomobject]@{path='app/WinSight.App.exe';bytes=$bytes.Length;sha256=Get-NativeHash (Join-Path $stage 'app/WinSight.App.exe')})
 [pscustomobject]@{schema=1;runtime='win-x64';selfContained=$true;releaseApproved=$false;appOnlyPreview=$true;files=$files}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
 [pscustomobject]@{path=$stage;hash=Get-NativeHash (Join-Path $stage 'release-manifest.json')}
}
try{
 $one=MakeStage s1 31;$two=MakeStage s2 67
 $data=Join-Path $root 'data';[IO.Directory]::CreateDirectory($data)|Out-Null
 [IO.File]::WriteAllText((Join-Path $data 'schema5-profile-settings-index-models'),'synthetic untouched')
 $dataHash=Get-NativeHash (Join-Path $data 'schema5-profile-settings-index-models')
 $cases=@('copy','state-partial','state-complete','journal-complete')
 foreach($case in $cases){
  $install=Join-Path $root $case
  $first=Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $one.path -ManifestSha256 $one.hash
  $old=$first.stateSha256
  $event=switch($case){copy{'DuringCopy'} state-partial{'DuringStateWrite'} state-complete{'AfterStateTemporary'} journal-complete{'AfterJournalTemporary'}}
  if($Before){$event=if($case -eq 'copy'){'AfterFirstCopy'}elseif($case -eq 'journal-complete'){'AfterJournal'}else{'AfterCommit'}}
  $hook={param($point);if($point -eq $event){throw "fixture interruption $event"}}.GetNewClosure()
  Reject {Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.path -ManifestSha256 $two.hash -ExpectedStateSha256 $old -TestHook $hook} ('physical interruption '+$case)
  $journal=Join-Path $install 'journal.json'
  if($Before){
   if($case -eq 'copy'){$file=Join-Path $install ('versions/'+$two.hash+'/app/WinSight.App.exe');$stream=[IO.File]::OpenWrite($file);try{$stream.SetLength(65536)}finally{$stream.Dispose()}}
   elseif($case.StartsWith('state')){$raw=[IO.File]::ReadAllBytes((Join-Path $install 'state.json'));if($case -eq 'state-partial'){$raw=$raw[0..63]};[IO.File]::WriteAllBytes((Join-Path $install 'state.json.new'),$raw)}
   else{[IO.File]::Move($journal,$journal+'.new')}
  }
  $journalSource=if(Test-Path -LiteralPath $journal){$journal}else{$journal+'.new'}
  $hash=Get-NativeHash $journalSource
  if($Before){Reject {Invoke-NativeRecovery $install $data $hash} ('BEFORE target failure '+$case);$observed.Add($case);continue}
  $evidenceFile=$null
  if($case -eq 'copy'){$evidenceFile=Join-Path $install ('copying/'+$hash+'/app/WinSight.App.exe');Check ((Get-Item -LiteralPath $evidenceFile).Length -eq 65536) 'actual first stream chunk is truncated before completion'}
  elseif($case.StartsWith('state')){$evidenceFile=Join-Path $install 'state.json.new';Check (Test-Path -LiteralPath $evidenceFile) ('actual atomic temporary survives '+$case)}
  $evidenceHash=if($evidenceFile){Get-NativeHash $evidenceFile}else{$null}
  Reject {Invoke-NativeRecovery $install (Join-Path $root 'wrong-data') $hash} ('wrong target refused '+$case)
  Reject {Invoke-NativeRecovery $install $data ('0'*64)} ('wrong journal hash refused '+$case)
  $unknown=Join-Path $install 'foreign.new';[IO.File]::WriteAllText($unknown,'external bytes')
  Reject {Invoke-NativeRecovery $install $data $hash} ('unknown .new refused '+$case)
  Check ([IO.File]::ReadAllText($unknown) -ceq 'external bytes') 'unknown data was not deleted'
  [IO.File]::Delete($unknown)
  if($case -eq 'copy'){
   $removed=Join-Path $install 'removed';[IO.Directory]::CreateDirectory($removed)|Out-Null
   [IO.File]::WriteAllText((Join-Path $removed 'foreign.txt'),'preserve')
   Reject {Invoke-NativeRecovery $install $data $hash} 'Apply recovery does not whitelist uninstall removed paths'
   Check ([IO.File]::ReadAllText((Join-Path $removed 'foreign.txt')) -ceq 'preserve') 'unrelated removed bytes remain intact'
   [IO.File]::Delete((Join-Path $removed 'foreign.txt'));[IO.Directory]::Delete($removed)
   $link=Join-Path $install ('copying/'+$hash+'/app/link');New-Item -ItemType Junction -Path $link -Target $data|Out-Null
   Reject {Invoke-NativeRecovery $install $data $hash} 'pending reparse entry rejected'
   [IO.Directory]::Delete($link)
   $raw=[IO.File]::ReadAllBytes($journalSource);[IO.File]::AppendAllText($journalSource,' ')
   Reject {Invoke-NativeRecovery $install $data $hash} 'journal tampering still rejected'
   [IO.File]::WriteAllBytes($journalSource,$raw)
  }
  if($case -eq 'state-partial'){
   $published=Join-Path $install ('versions/'+$two.hash+'/app/WinSight.App.exe');$raw=[IO.File]::ReadAllBytes($published)
   $changed=[byte[]]$raw.Clone();$changed[0]=99;[IO.File]::WriteAllBytes($published,$changed)
   Reject {Invoke-NativeRecovery $install $data $hash} 'published candidate tampering is not treated as an incomplete staging write'
   Check ((Get-Item -LiteralPath $published).Length -eq $raw.Length) 'tampered final candidate not deleted'
   [IO.File]::WriteAllBytes($published,$raw)
  }
  $result=Invoke-NativeRecovery $install $data $hash
  Check ((Get-NativeHash (Join-Path $install 'state.json')) -ceq $old) ('verified prior state restored '+$case)
  Check ((Get-NativeHash (Join-Path $install ('versions/'+$one.hash+'/app/WinSight.App.exe'))) -ceq (Get-NativeHash (Join-Path $one.path 'app/WinSight.App.exe'))) ('prior binary exact '+$case)
  if($evidenceFile){
   $saved=if($case -eq 'copy'){Join-Path $result.quarantine 'copying/app/WinSight.App.exe'}else{Join-Path $result.quarantine 'state.json.new'}
   Check ((Get-NativeHash $saved) -ceq $evidenceHash) ('uncertain bytes preserved without deletion '+$case)
   Check ($result.journalRetained -and $result.quarantine.EndsWith($hash)) ('quarantine identity bound to retained trusted journal '+$case)
   Reject {Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.path -ManifestSha256 $two.hash -ExpectedStateSha256 $old} 'quarantine requires explicit disposition, no silent replay'
  }else{Check (-not (Test-Path -LiteralPath $journal) -and -not (Test-Path -LiteralPath ($journal+'.new'))) 'trusted complete journal temporary resolves without blanket whitelist'}
 }
 $initial=Join-Path $root 'init';[IO.Directory]::CreateDirectory((Join-Path $initial 'versions'))|Out-Null
 if($Before){Reject {Invoke-NativeTransaction -Operation Apply -InstallRoot $initial -DataRoot $data -StageDirectory $one.path -ManifestSha256 $one.hash} 'BEFORE exact empty versions boundary stranded';$observed.Add('empty-versions')}
 else{
  $fresh=Invoke-NativeTransaction -Operation Apply -InstallRoot $initial -DataRoot $data -StageDirectory $one.path -ManifestSha256 $one.hash
  Check ($fresh.current -ceq $one.hash) 'legacy exact empty versions-only boundary can initialize without deletion'
  $partial=Join-Path $root 'journal-partial';$hook={param($event);if($event -eq 'DuringJournalWrite'){throw 'partial journal'}}
  Reject {Invoke-NativeTransaction -Operation Apply -InstallRoot $partial -DataRoot $data -StageDirectory $one.path -ManifestSha256 $one.hash -TestHook $hook} 'actual partial first journal write'
  Check (-not(Test-Path -LiteralPath (Join-Path $partial 'versions'))) 'no versions or payload is created before a valid journal is published'
  $part=Join-Path $partial 'journal.json.new';$partHash=Get-NativeHash $part
  Reject {Invoke-NativeRecovery $partial $data ('0'*64)} 'unauthenticated/truncated first journal cannot waive trust guard'
  Check ((Get-NativeHash $part) -ceq $partHash) 'untrusted partial first journal evidence remains intact'
 }
 Check ((Get-NativeHash (Join-Path $data 'schema5-profile-settings-index-models')) -ceq $dataHash) 'synthetic profile/data unchanged'
 [pscustomobject]@{before=[bool]$Before;checks=@($checks);reproducedFailures=@($observed);moduleSha256=Get-NativeHash $ModulePath;actualSetupExecuted=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $root 'summary.json') -Encoding utf8
 Write-Output "PASS $($checks.Count) targeted checks; before=$Before; no setup."
}finally{Stop-Transcript|Out-Null}
