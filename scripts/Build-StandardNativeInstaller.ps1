[CmdletBinding(DefaultParameterSetName='Build')]
param(
    [Parameter(Mandatory,ParameterSetName='Build')][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [Parameter(Mandatory,ParameterSetName='Prepare')][switch]$PrepareControl,
    [Parameter(Mandatory,ParameterSetName='Prepare')][ValidateNotNullOrEmpty()][string]$BundleDirectory,
    [Parameter(Mandatory,ParameterSetName='Prepare')][ValidatePattern('^[0-9a-f]{64}$')][string]$BundleSha256,
    [ValidateRange(1000,160000)][int]$TimeoutMilliseconds=160000
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
throw 'B.UnconfiguredPreviewSource: this source-only prototype must be materialized and re-pinned in a private build graph; it is not a public install command.'
$start=[long][Environment]::TickCount -band 4294967295L
$utf8=[Text.UTF8Encoding]::new($false,$true)
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.ToString() -cne '5.1.29683.1000' -or -not [Environment]::Is64BitProcess) { throw 'B.Host: exact x64 Windows PowerShell 5.1.29683.1000 required.' }
$engine=Join-Path (Split-Path $PSScriptRoot -Parent) 'desktop\installer'
$repository='__WINSIGHT_PRIVATE_REPOSITORY_ROOT__'
$task=$repository+'\.dev\native-migration\release-acceptance\standard-install-checks\run-01'
$control=$task+'\control'
$stage=$repository+'\.dev\p6\.dev\r1\stage'
$manifestHash='7799fd36eb169b94252a4a5292ba6275208d4a02f887ca35ffafbc487d9c3e14'
$compiler=$repository+'\.dev\native-migration\release-acceptance\dependencies\tools\inno-6.7.3\ISCC.exe'
$compilerHash='0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7'
$setupName='WinSight-Standard-Native-Candidate.exe'
$slots=@('NativeInstaller.psm1','Invoke-NativeInstaller.ps1','NativeDeploymentJob.psm1','NativeInstallCoordinator.psm1','Invoke-NativeInstallCoordinator.ps1','NativeStandardLiveShell.psm1','Invoke-NativeDeployment.ps1','Invoke-NativeDeployWorker.ps1','Start-WinSightNative.ps1')
function Assert-Path([string]$Path) {
    if ($Path -cnotmatch '^[A-Z]:\\' -or $Path -match '[\x00-\x1f"<>|?*@{}]' -or $Path.Substring(2).Contains(':') -or [IO.Path]::GetFullPath($Path).TrimEnd('\') -cne $Path) { throw 'B.Path: canonical unambiguous local path required.' }
    foreach($part in $Path.Substring(3).Split('\')){if(-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)'){throw 'B.Path: ambiguous segment.'}}
    for($p=$Path;$p;$p=[IO.Path]::GetDirectoryName($p)){
        try{$a=[IO.File]::GetAttributes($p)}
        catch [IO.FileNotFoundException]{continue}
        catch [IO.DirectoryNotFoundException]{continue}
        if($a -band [IO.FileAttributes]::ReparsePoint){throw 'B.Path: reparse rejected.'}
    }
}
function Assert-Absent([string]$Path) {
    Assert-Path $Path
    try{$null=[IO.File]::GetAttributes($Path)}
    catch [IO.FileNotFoundException]{return}
    catch [IO.DirectoryNotFoundException]{return}
    throw ('B.Existing: will not adopt or overwrite '+$Path)
}
# No JSON-selected module is imported. These are frozen source identities.
foreach($row in @(
    @('NativeDeploymentJob.psm1','1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331'),
    @('NativeStandardLiveShell.psm1','7aa3a888133ed4792c3e1e923ff7e11b4ddf7465af6cecbc634fea5121410485'),
    @('NativeInstaller.psm1','768e725b9ea3e5193b4d87d2430a63801c98af5b876f1328c31e49c60500c572')
)){
    $p=Join-Path $engine $row[0];Assert-Path $p
    if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row[1]){throw 'B.Bootstrap: frozen source mismatch.'}
}
Import-Module (Join-Path $engine 'NativeDeploymentJob.psm1') -ErrorAction Stop
$adapter=Import-Module (Join-Path $engine 'NativeStandardLiveShell.psm1') -PassThru -ErrorAction Stop
function Budget { if((Get-NativeDeadlineRemaining $start $TimeoutMilliseconds) -le 0){throw 'B.Deadline: shared build/prepare budget exhausted; partial state retained.'} }
function Pin([string]$Path) { Budget; & $adapter {param($P,$S,$D) Get-NLFilePin $P $S $D} $Path $start $TimeoutMilliseconds }
function Assert-Pin($Expected,[string]$ActualPath) {
    & $adapter {param($P) Assert-NLPin $P} $Expected
    $actual=Pin $ActualPath
    if($actual.bytes -ne $Expected.bytes -or $actual.sha256 -cne $Expected.sha256){throw ('B.Pin: bytes changed '+$ActualPath)}
}
function Bytes($Value) { ,$utf8.GetBytes(($Value|ConvertTo-Json -Depth 30)) }
function Hash([byte[]]$Value) { & $adapter {param($B) Get-NLHash $B} $Value }
function Write-New([string]$Path,[byte[]]$Value) {
    Budget;Assert-Absent $Path;Assert-Path ([IO.Path]::GetDirectoryName($Path))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null
    $s=[IO.File]::Open($Path,'CreateNew','Write','None')
    try{$s.Write($Value,0,$Value.Length);$s.Flush($true)}finally{$s.Dispose()}
    Budget
}
function Assert-StreamPin($Expected,[IO.FileStream]$Stream) {
    & $adapter {param($P) Assert-NLPin $P} $Expected
    Budget
    if($Stream.Length -ne $Expected.bytes){throw 'B.Pin: retained source length changed.'}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{
        $Stream.Position=0;$total=0L;$buffer=New-Object byte[] 65536
        while(($n=$Stream.Read($buffer,0,$buffer.Length)) -gt 0){Budget;$total+=$n;[void]$sha.TransformBlock($buffer,0,$n,$buffer,0)}
        Budget;[void]$sha.TransformFinalBlock($buffer,0,0)
        if($total -ne $Expected.bytes -or $Stream.Length -ne $Expected.bytes -or [BitConverter]::ToString($sha.Hash).Replace('-','').ToLowerInvariant() -cne $Expected.sha256){throw 'B.Pin: retained source bytes changed.'}
        $Stream.Position=0
    }finally{$sha.Dispose()}
}
function Copy-Pin($Expected,[string]$Source,[string]$Destination,[IO.FileStream]$RetainedSource=$null) {
    Budget;Assert-Path $Source
    $inputStream=$RetainedSource;$ownsStream=($null -eq $RetainedSource)
    if($ownsStream){$inputStream=[IO.File]::Open($Source,'Open','Read','Read')}
    try{
        # B-SOURCE-COPY-TOCTOU-01: validation and copy share one write/delete-denying handle.
        Assert-StreamPin $Expected $inputStream
        Budget;Assert-Absent $Destination
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))|Out-Null
        $outputStream=[IO.File]::Open($Destination,'CreateNew','Write','None')
        try{$buffer=New-Object byte[] 65536;while(($n=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){Budget;$outputStream.Write($buffer,0,$n)};$outputStream.Flush($true)}finally{$outputStream.Dispose()}
    }finally{if($ownsStream){$inputStream.Dispose()}}
    Assert-Pin $Expected $Destination
}
function Get-Sources {
    $files=@(foreach($name in $slots){$p=Pin (Join-Path $engine $name);[pscustomobject]@{name=$name;bytes=$p.bytes;sha256=$p.sha256}})
    $value=[pscustomobject]@{protocol='StandardSources/1';files=$files}
    & $adapter {param($V) Assert-NLSources $V} $value
    $value
}
function Read-Json([string]$Path,[string]$ExpectedHash) { & $adapter {param($P,$H,$S,$D) Read-NLJson $P $H $S $D} $Path $ExpectedHash $start $TimeoutMilliseconds }
function Read-PayloadManifest {
    $path=$stage+'\release-manifest.json';Budget;Assert-Path $path
    $stream=[IO.File]::Open($path,'Open','Read','Read');$transferred=$false
    try{
        if($stream.Length -gt 2097152){throw 'B.Payload: manifest JSON bound exceeded.'}
        $rawBytes=New-Object byte[] ([int]$stream.Length);$offset=0
        while($offset -lt $rawBytes.Length){Budget;$n=$stream.Read($rawBytes,$offset,[Math]::Min(65536,$rawBytes.Length-$offset));if($n -eq 0){throw 'B.Payload: manifest short read.'};$offset+=$n}
        if($stream.Length -ne $rawBytes.Length -or (Hash $rawBytes) -cne $manifestHash){throw 'B.Payload: original approved manifest bytes required.'}
        $m=& $adapter {param($B,$S,$D) ConvertFrom-NLBytes $B $S $D} $rawBytes $start $TimeoutMilliseconds
        # B-SOURCE-MANIFEST-PIN-01: never replace the original raw-byte pin with a later path observation.
        $pin=[pscustomobject]@{path=$path;bytes=[long]$rawBytes.Length;sha256=$manifestHash}
        Assert-StreamPin $pin $stream
        $result=[pscustomobject]@{manifest=$m;pin=$pin;rawBytes=$rawBytes;stream=$stream}
        $transferred=$true
        $result
    }finally{if(-not $transferred){$stream.Dispose()}}
}
function Get-Payload {
    $lease=Read-PayloadManifest;$transferred=$false
    try{
    $m=$lease.manifest
    $inventory=& $adapter {param($M) Assert-NLInventory $M} $m
    if($inventory.fileCount -ne 832 -or $inventory.totalBytes -ne 939999759L){throw 'B.Payload: exact frozen832 required.'}
    & $adapter {param($I,$R,$S,$D) Assert-NLPayloadCoverage $I $R $S $D} $inventory $stage $start $TimeoutMilliseconds
    $rootEntries=@([IO.Directory]::EnumerateFileSystemEntries($stage))
    if($rootEntries.Count -ne 2 -or @($rootEntries|Where-Object {[IO.Path]::GetFileName($_) -cnotin @('app','release-manifest.json')}).Count){throw 'B.Payload: unknown stage root entry.'}
    foreach($file in $m.files){Assert-Pin ([pscustomobject]@{path=($stage+'\'+$file.path.Replace('/','\'));bytes=$file.bytes;sha256=$file.sha256}) ($stage+'\'+$file.path.Replace('/','\'))}
    $result=[pscustomobject]@{manifest=$m;inventory=$inventory;pin=$lease.pin;rawBytes=$lease.rawBytes;stream=$lease.stream}
    $transferred=$true
    $result
    }finally{if(-not $transferred){$lease.stream.Dispose()}}
}
function Assert-NewTarget {
    Budget;Assert-Absent $task
    $menu=[IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'),'Microsoft\Windows\Start Menu\Programs\WinSight Native Preview')
    Assert-Absent $menu
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
    try{$key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1',$false);if($null -ne $key){$key.Dispose();throw 'B.ForeignRegistration: exact key already exists.'}}finally{$base.Dispose()}
    Budget
}
function Make-Graph($Sources,$Payload,$SetupPin,$AssemblyPin,$HostRecord) {
    $sourceBytes=Bytes $Sources;$sourceHash=Hash $sourceBytes
    $sourcePin=[pscustomobject]@{path=($control+'\objects\'+$sourceHash+'.json');bytes=[long]$sourceBytes.Length;sha256=$sourceHash}
    $roots=[pscustomobject]@{taskRoot=$task;installRoot=($task+'\install');shellRoot=($task+'\install\shell');deploymentRoot=($task+'\install\deployment');dataRoot=($task+'\data');controlRoot=$control}
    $payloadPin=[pscustomobject]@{path=($control+'\stage\'+$manifestHash+'\release-manifest.json');bytes=$Payload.pin.bytes;sha256=$manifestHash}
    $launch=[pscustomobject]@{
        app=($Payload.inventory.map['app/WinSight.App.exe']);cli=($Payload.inventory.map['app/winsight-native.exe']);
        helpers=@($Payload.manifest.files|Where-Object {$_.path -cin @('app/inference/WinSight.InferenceHost.exe','app/inference/onnxruntime.dll','app/inference/runtimes/win-x64/native/winsight_tokenizer.dll')});
        models=@($Payload.manifest.files|Where-Object {$_.path.StartsWith('app/models/',[StringComparison]::Ordinal) -and $_.path -cnotmatch '(config|processor_config)\.json$'});
        profiles=@($Payload.manifest.files|Where-Object {$_.path -ceq 'app/inference/model-manifest.json' -or $_.path -ceq 'app/THIRD-PARTY-NOTICES.txt' -or ($_.path.StartsWith('app/models/',[StringComparison]::Ordinal) -and $_.path -cmatch '(config|processor_config)\.json$')})
    }
    $package=[pscustomobject]@{protocol='StandardPackage/1';setup=$SetupPin;payloadManifest=$payloadPin;fileCount=$Payload.inventory.fileCount;totalBytes=$Payload.inventory.totalBytes;sourceManifest=$sourcePin;assemblyEvidence=$AssemblyPin;launch=$launch;appOnlyPreview=$false;releaseApproved=$false}
    $packageBytes=Bytes $package;$packageHash=Hash $packageBytes
    $binding=[pscustomobject]@{schema=3;backend='InnoPerUserLive/1';purpose='InternalStandardCandidate';releaseApproved=$false;instanceId='run-01';appId='WinSight.Native.Preview';displayName='WinSight Native Preview';roots=$roots;host=$HostRecord;sources=$sourcePin;
        package=[pscustomobject]@{path=($control+'\objects\'+$packageHash+'.json');bytes=[long]$packageBytes.Length;sha256=$packageHash};shellSpec=$null;priorReceipt=$null}
    $commands=& $adapter {param($B) Get-NLShellCommands $B} $binding
    $values=@(foreach($row in @(@('DisplayName','WinSight Native Preview'),@('DisplayVersion','0.0.0'),@('Publisher','WinSight'),@('InstallLocation',$roots.installRoot),@('UninstallString',$commands.full),@('QuietUninstallString',$commands.fullQuiet))){[pscustomobject]@{name=$row[0];kind='String';value=$row[1]}})
    $values+=@([pscustomobject]@{name='NoModify';kind='DWord';value=1},[pscustomobject]@{name='NoRepair';kind='DWord';value=1})
    $launcher=@($Sources.files|Where-Object name -CEQ 'Start-WinSightNative.ps1')[0]
    $spec=[pscustomobject]@{protocol='StandardShellSpec/1';registry=[pscustomobject]@{hive='HKCU';view='Registry64';key='Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1';values=$values};
        menu=[pscustomobject]@{path=([IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'),'Microsoft\Windows\Start Menu\Programs\WinSight Native Preview'));links=@(
            [pscustomobject]@{name='WinSight Native Preview.lnk';target=$HostRecord.executable.path;arguments=$commands.launch;workingDirectory=$roots.shellRoot},
            [pscustomobject]@{name='Uninstall WinSight Native Preview.lnk';target=$HostRecord.executable.path;arguments=$commands.uninstall;workingDirectory=$commands.runtime})};
        files=@([pscustomobject]@{path=($roots.shellRoot+'\Start-WinSightNative.ps1');bytes=$launcher.bytes;sha256=$launcher.sha256});uninstallerPaths=@(($roots.installRoot+'\unins000.exe'),($roots.installRoot+'\unins000.dat'))}
    $specBytes=Bytes $spec;$specHash=Hash $specBytes
    $binding.shellSpec=[pscustomobject]@{path=($control+'\objects\'+$specHash+'.json');bytes=[long]$specBytes.Length;sha256=$specHash}
    & $adapter {param($B,$P,$I,$S,$Sources) Assert-NLBinding $B;Assert-NLPackage $P $B $I;Assert-NLShellSpec $S $B $Sources} $binding $package $Payload.inventory $spec $Sources
    $bindingBytes=Bytes $binding;$bindingHash=Hash $bindingBytes
    [pscustomobject]@{sources=$Sources;sourceBytes=$sourceBytes;sourcePin=$sourcePin;packageBytes=$packageBytes;packagePin=$binding.package;specBytes=$specBytes;specPin=$binding.shellSpec;bindingBytes=$bindingBytes;bindingPin=[pscustomobject]@{path=($control+'\bindings\'+$bindingHash+'.json');bytes=[long]$bindingBytes.Length;sha256=$bindingHash}}
}
function Get-ConfigBytes([string]$Bundle,$HostRecord,[string]$SourceHash,$Sources) {
    & $adapter {param($S,$H) Assert-NLSources $S;Assert-NLHash $H} $Sources $HostRecord.executable.sha256
    $definitions=[ordered]@{BoundTaskRoot=$task;BoundControlRoot=$control;BoundInstallRoot=($task+'\install');BoundHost=$HostRecord.executable.path;BoundHostHash=$HostRecord.executable.sha256;RuntimeRoot=($control+'\runtime\'+$SourceHash);CandidateOutput=$Bundle;LauncherSource=($Bundle+'\runtime\'+$SourceHash+'\Start-WinSightNative.ps1')}
    for($index=0;$index -lt $slots.Count;$index++){
        $source=@($Sources.files|Where-Object {$_.name -ceq $slots[$index]})[0]
        $definitions['SourceHash'+$index]=$source.sha256
    }
    $lines=@(foreach($name in $definitions.Keys){'#define {0} "{1}"' -f $name,$definitions[$name]})
    ,$utf8.GetBytes(($lines -join "`r`n"))
}
function Host-Record {
    $process=[Diagnostics.Process]::GetCurrentProcess()
    try{$exe=$process.MainModule.FileName}finally{$process.Dispose()}
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{$sid=$identity.User.Value}finally{$identity.Dispose()}
    [pscustomobject]@{edition='Desktop';version=$PSVersionTable.PSVersion.ToString();executable=(Pin $exe);machine=[Environment]::MachineName;userSid=$sid;serializer='ConvertTo-Json/Depth30/UTF8-noBOM'}
}
$sources=Get-Sources
$sourceBytes=Bytes $sources;$sourceHash=Hash $sourceBytes
$payload=Get-Payload
try{
$hostRecord=Host-Record
$compilerPin=Pin $compiler
# This official ISCC has FileVersion 0.0.0.0; its independently frozen hash is the identity fence.
if($compilerPin.sha256 -cne $compilerHash){throw 'B.Compiler: exact existing official Inno6.7.3 required.'}
$assemblySources=@(foreach($path in @((Join-Path $engine 'WinSight.Standard.Native.iss'),$PSCommandPath,(Join-Path $engine 'Invoke-NativeCompilerProcess.ps1'))){$pin=Pin $path;[pscustomobject]@{name=[IO.Path]::GetFileName($path);bytes=$pin.bytes;sha256=$pin.sha256}})
if($PSCmdlet.ParameterSetName -ceq 'Build'){
    Assert-Absent $OutputDirectory
    foreach($reserved in @($task,$stage,$engine,[IO.Path]::GetDirectoryName($compiler))){if($OutputDirectory.StartsWith($reserved+'\',[StringComparison]::OrdinalIgnoreCase) -or $reserved.StartsWith($OutputDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or $reserved -ieq $OutputDirectory){throw 'B.Output: disjoint new bundle required.'}}
    $bundle=$OutputDirectory
    Assert-StreamPin $payload.pin $payload.stream
    Write-New ($bundle+'\sources.json') $sourceBytes
    foreach($source in $sources.files){Copy-Pin ([pscustomobject]@{path=($engine+'\'+$source.name);bytes=$source.bytes;sha256=$source.sha256}) ($engine+'\'+$source.name) ($bundle+'\runtime\'+$sourceHash+'\'+$source.name)}
    foreach($source in $assemblySources){$path=if($source.name -ceq 'Build-StandardNativeInstaller.ps1'){$PSCommandPath}else{Join-Path $engine $source.name};Copy-Pin ([pscustomobject]@{path=$path;bytes=$source.bytes;sha256=$source.sha256}) $path ($bundle+'\provenance\'+$source.name)}
    Write-New ($bundle+'\standard-config.iss') (Get-ConfigBytes $bundle $hostRecord $sourceHash $sources)
    $remaining=(Get-NativeDeadlineRemaining $start $TimeoutMilliseconds)-20000
    if($remaining -lt 1000){throw 'B.Deadline: insufficient compiler/verification reserve.'}
    $receipt=& (Join-Path $engine 'Invoke-NativeCompilerProcess.ps1') -CompilerPath $compiler -CompilerArguments ('/DGeneratedConfig="'+$bundle+'\standard-config.iss" "'+$bundle+'\provenance\WinSight.Standard.Native.iss"') -LogPath ($bundle+'\compile.log') -ProcessTimeoutMilliseconds ([int][Math]::Min(130000,$remaining)) -SettlementTimeoutMilliseconds 2000 -DrainTimeoutMilliseconds 2000
    if($receipt.exitCode -isnot [int] -or $receipt.exitCode -ne 0 -or -not $receipt.drained -or -not $receipt.readersSettled -or -not $receipt.processSettled){throw 'B.Compiler: actual Int32 exit0/dualEOF required.'}
}else{
    if(-not $PrepareControl){throw 'B.Prepare: false selector rejected.'}
    $bundle=$BundleDirectory;Assert-Path $bundle
    $record=Read-Json ($bundle+'\bundle.json') $BundleSha256
    & $adapter {param($R) Assert-NLFields $R @('protocol','binding','sourceManifestSha256','setup','assembly','compilerReceipt','releaseApproved','standaloneConsumerRelease','legalApproved');foreach($N in @('binding','setup','assembly','compilerReceipt')){Assert-NLPin $R.$N};Assert-NLHash $R.sourceManifestSha256} $record
    if($record.protocol -cne 'StandardCandidateBundle/1' -or $record.sourceManifestSha256 -cne $sourceHash -or $record.releaseApproved -isnot [bool] -or $record.releaseApproved -or $record.standaloneConsumerRelease -isnot [bool] -or $record.standaloneConsumerRelease -or $record.legalApproved -isnot [bool] -or $record.legalApproved){throw 'B.Bundle: current sources/internal-unapproved bundle required.'}
    Assert-Pin $record.setup ($bundle+'\'+$setupName)
    Assert-Pin $record.compilerReceipt ($bundle+'\compile.log.process.json')
    $receipt=Get-Content -LiteralPath ($bundle+'\compile.log.process.json') -Raw|ConvertFrom-Json
    if(($receipt.exitCode -isnot [int] -and $receipt.exitCode -isnot [long]) -or $receipt.exitCode -ne 0 -or $receipt.drained -isnot [bool] -or -not $receipt.drained -or $receipt.readersSettled -isnot [bool] -or -not $receipt.readersSettled -or $receipt.processSettled -isnot [bool] -or -not $receipt.processSettled -or @($receipt.cleanupErrors).Count -or $receipt.error){throw 'B.Bundle: failed/partial compiler receipt.'}
}
Budget
# Recheck final inputs after compile, not merely their initial manifest.
if((Hash (Bytes (Get-Sources))) -cne $sourceHash){throw 'B.SourceChanged: final nine changed during operation.'}
Assert-Pin $compilerPin $compiler
foreach($row in $assemblySources){
    $p=if($row.name -ceq 'Build-StandardNativeInstaller.ps1'){$PSCommandPath}else{Join-Path $engine $row.name}
    $pin=[pscustomobject]@{path=$p;bytes=$row.bytes;sha256=$row.sha256}
    Assert-Pin $pin $p
    Assert-Pin $pin ($bundle+'\provenance\'+$row.name)
}
foreach($source in $sources.files){Assert-Pin ([pscustomobject]@{path=($engine+'\'+$source.name);bytes=$source.bytes;sha256=$source.sha256}) ($bundle+'\runtime\'+$sourceHash+'\'+$source.name)}
$configBytes=Get-ConfigBytes $bundle $hostRecord $sourceHash $sources
Assert-Pin ([pscustomobject]@{path=($bundle+'\standard-config.iss');bytes=[long]$configBytes.Length;sha256=(Hash $configBytes)}) ($bundle+'\standard-config.iss')
$setupActual=Pin ($bundle+'\'+$setupName)
$setupPin=[pscustomobject]@{path=($control+'\artifacts\'+$setupName);bytes=$setupActual.bytes;sha256=$setupActual.sha256}
$compilerReceipt=Pin ($bundle+'\compile.log.process.json')
$assembly=[pscustomobject]@{protocol='StandardAssemblyEvidence/1';compiler=$compilerPin;compilerReceiptSha256=$compilerReceipt.sha256;sourceManifestSha256=$sourceHash;assemblySources=$assemblySources;generatedConfigSha256=(Hash $configBytes);setup=$setupPin;payloadManifestSha256=$manifestHash;fileCount=832;totalBytes=939999759L;productRebuilt=$false;originalPayloadBuilderSucceeded=$false;legalApproved=$false;releaseApproved=$false;standaloneConsumerRelease=$false}
$assemblyBytes=Bytes $assembly;$assemblyHash=Hash $assemblyBytes
$assemblyPin=[pscustomobject]@{path=($control+'\artifacts\assembly.json');bytes=[long]$assemblyBytes.Length;sha256=$assemblyHash}
$graph=Make-Graph $sources $payload $setupPin $assemblyPin $hostRecord
if($PSCmdlet.ParameterSetName -ceq 'Build'){
    Write-New ($bundle+'\assembly.json') $assemblyBytes
    foreach($row in @(@('sources',$graph.sourcePin,$graph.sourceBytes),@('package',$graph.packagePin,$graph.packageBytes),@('shell',$graph.specPin,$graph.specBytes))){Write-New ($bundle+'\objects\'+$row[1].sha256+'.json') $row[2]}
    Write-New ($bundle+'\bindings\'+$graph.bindingPin.sha256+'.json') $graph.bindingBytes
    $record=[pscustomobject]@{protocol='StandardCandidateBundle/1';binding=$graph.bindingPin;sourceManifestSha256=$sourceHash;setup=$setupPin;assembly=$assemblyPin;compilerReceipt=$compilerReceipt;releaseApproved=$false;standaloneConsumerRelease=$false;legalApproved=$false}
    Write-New ($bundle+'\bundle.json') (Bytes $record)
    [pscustomobject]@{status='CompiledOnly';bundle=($bundle+'\bundle.json');bundleSha256=(Pin ($bundle+'\bundle.json')).sha256;binding=$graph.bindingPin;controlPrepared=$false;installed=$false;releaseApproved=$false}|ConvertTo-Json -Depth 5
}else{
    if($record.binding.path -cne $graph.bindingPin.path -or $record.binding.sha256 -cne $graph.bindingPin.sha256 -or $record.binding.bytes -ne $graph.bindingPin.bytes -or $record.assembly.sha256 -cne $assemblyHash){throw 'B.Bundle: exact current graph/assembly mismatch.'}
    Assert-Pin $assemblyPin ($bundle+'\assembly.json')
    foreach($source in $sources.files){Assert-Pin ([pscustomobject]@{path=($engine+'\'+$source.name);bytes=$source.bytes;sha256=$source.sha256}) ($bundle+'\runtime\'+$sourceHash+'\'+$source.name)}
    foreach($pin in @($graph.sourcePin,$graph.packagePin,$graph.specPin)){Assert-Pin $pin ($bundle+'\objects\'+$pin.sha256+'.json')}
    Assert-Pin $graph.bindingPin ($bundle+'\bindings\'+$graph.bindingPin.sha256+'.json')
    # Initial preparation only. Existing/pending/foreign state is not adopted or repaired.
    Assert-NewTarget
    Assert-StreamPin $payload.pin $payload.stream
    [IO.Directory]::CreateDirectory($control)|Out-Null
    foreach($source in $sources.files){Copy-Pin ([pscustomobject]@{path=($engine+'\'+$source.name);bytes=$source.bytes;sha256=$source.sha256}) ($bundle+'\runtime\'+$sourceHash+'\'+$source.name) ($control+'\runtime\'+$sourceHash+'\'+$source.name)}
    $targetStage=$control+'\stage\'+$manifestHash
    foreach($file in $payload.manifest.files){$relative=$file.path.Replace('/','\');Copy-Pin ([pscustomobject]@{path=($stage+'\'+$relative);bytes=$file.bytes;sha256=$file.sha256}) ($stage+'\'+$relative) ($targetStage+'\'+$relative)}
    Copy-Pin $payload.pin ($stage+'\release-manifest.json') ($targetStage+'\release-manifest.json') $payload.stream
    Copy-Pin $setupPin ($bundle+'\'+$setupName) $setupPin.path
    Write-New $assemblyPin.path $assemblyBytes
    foreach($row in @(@($graph.sourcePin,$graph.sourceBytes),@($graph.packagePin,$graph.packageBytes),@($graph.specPin,$graph.specBytes))){Write-New $row[0].path $row[1]}
    Write-New $graph.bindingPin.path $graph.bindingBytes
    $null=Read-NativeLiveGraph -BindingPath $graph.bindingPin.path -BindingSha256 $graph.bindingPin.sha256 -StartTick $start -DeadlineMilliseconds $TimeoutMilliseconds
    [pscustomobject]@{status='ControlPreparedNotInstalled';binding=$graph.bindingPin;stageFileCount=832;stageBytes=939999759L;installed=$false;legalApproved=$false;releaseApproved=$false}|ConvertTo-Json -Depth 5
}
}finally{$payload.stream.Dispose()}
