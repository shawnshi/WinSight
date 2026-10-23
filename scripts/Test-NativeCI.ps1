#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [ValidateSet('Plan','Build')][string]$Mode = 'Build',
    [ValidateSet('Offline','PublicHosted')][string]$DependencyMode = 'Offline',
    [string]$NuGetPackageRoot,
    [string]$CargoCacheRoot,
    [switch]$CheckpointWorker
)
$ErrorActionPreference = 'Stop'
$script:invocationClock = [Diagnostics.Stopwatch]::StartNew()
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'native-ci/NativeCI.Common.psm1') -Force
$repository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Import-Module (Join-Path $PSScriptRoot 'native-ci/NativeCI.Checkpoint.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'native-ci/NativeCI.Outputs.psm1') -Force
if ($CheckpointWorker) {
    if ($env:WINSIGHT_CI_CHECKPOINT_ROOT -ne $ArtifactRoot) { throw 'Use Invoke-NativeCICheckpoint.ps1; direct worker invocation is not supported.' }
    if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot)) { throw 'Checkpoint ArtifactRoot must be absolute.' }
    $out = Assert-NativeCIPlainPath $ArtifactRoot
    $boundary = Join-Path $repository '.dev/native-migration/native-ci'
    if (-not $out.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Checkpoint root is outside the CI artifact boundary.' }
}
else { $out = New-NativeCIArtifactRoot -RepositoryRoot $repository -ArtifactRoot $ArtifactRoot }
foreach ($folder in @('logs','fixtures','candidate')) { $null = New-Item -ItemType Directory -Path (Join-Path $out $folder) -Force }
$required = @(Get-NativeCIRequiredGates)
$gates = @($required | ForEach-Object { [pscustomobject]@{ name = $_; requiredForCI = $true; state = 'NotRun'; detail = 'Not executed.' } })
$gates += @(
    [pscustomobject]@{ name = 'ModelParity'; requiredForCI = $false; state = 'Blocked'; detail = 'Parent-prepared pinned models are not consumed in model-free CI; parity is separate and unverified.' },
    [pscustomobject]@{ name = 'InstallerRecovery'; requiredForCI = $false; state = 'Blocked'; detail = 'Parent-prepared compiler/full payload and actual recovery remain separate; no installer is compiled or executed here.' },
    [pscustomobject]@{ name = 'InteractiveAccessibility'; requiredForCI = $false; state = 'NotRun'; detail = 'Hosted CI does not launch privileged/interactive UI; current-machine UI/accessibility gate remains unverified.' },
    [pscustomobject]@{ name = 'FullMediaAcceptance'; requiredForCI = $false; state = 'NotRun'; detail = 'Required synthetic media gates do not prove model/reference parity or full P0-P6 acceptance.' }
)
$inventory = @(Get-NativeCISuiteInventory -RepositoryRoot $repository)
$commands = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
$workspace = Join-Path $out 'workspace'
$script:currentGate = $null
$state = 'incomplete'
$processEnvironment = @{
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'; DOTNET_CLI_TELEMETRY_OPTOUT='1';
    DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME; NUGET_PACKAGES=$NuGetPackageRoot; CARGO_HOME=$CargoCacheRoot;
    RUSTUP_AUTO_INSTALL='0'; CARGO_NET_OFFLINE=$(if ($DependencyMode -eq 'Offline') { 'true' } else { 'false' });
    MSBUILDDISABLENODEREUSE='1'; DOTNET_CLI_USE_MSBUILD_SERVER='0';
    TEMP=(Join-Path $out 'fixtures'); TMP=(Join-Path $out 'fixtures')
}
$emptyFeed = Join-Path $out 'empty-feed'; $nugetConfig = Join-Path $out 'NuGet.Config'
$script:commandCursor = 0; $script:newCommands = 0
$script:retainedOutputs = [pscustomobject]@{schema=1;files=@()}
if ($CheckpointWorker -and (Test-Path -LiteralPath (Join-Path $out 'checkpoint-state.json'))) {
    $saved = Read-NativeCICheckpoint $out -Clock $script:invocationClock
    $script:retainedOutputs = $saved.retainedOutputs
    if (($saved.gates.name -join '|') -ne ($gates.name -join '|') -or $saved.artifactRoot -ne $out) { throw 'Checkpoint gate/root contract changed.' }
    $gates = @($saved.gates); $state = $saved.state
    foreach ($command in $saved.commands) { $commands.Add($command) }
    foreach ($inputFile in $saved.inputHashes) { $inputs.Add($inputFile) }
}
function Save-Report {
    $report = [pscustomobject]@{ schema = 3; state = $state; mode = $Mode; dependencyMode = $DependencyMode;
        runtime = 'win-x64'; releaseApproved = $false; fullReleaseAcceptance = $false; artifactRoot = $out;
        scope = 'Native build and model-free synthetic candidates only; not a release or full P0-P6 acceptance.';
        gates = $gates; commands = $commands.ToArray(); inputHashes = $inputs.ToArray(); suiteInventory = $inventory; retainedOutputs = $script:retainedOutputs
    }
    if ($CheckpointWorker) { Save-NativeCICheckpoint $out $report }
    else { Write-NativeCIAtomicJson (Join-Path $out 'native-ci-result.json') $report }
}
function Invoke-Gate([string]$Name, [scriptblock]$Action) {
    $script:currentGate = $gates | Where-Object name -EQ $Name
    if ($CheckpointWorker -and $script:currentGate.state -eq 'Passed') {
        $script:commandCursor += @($commands | Where-Object gate -EQ $Name).Count
        return
    }
    $script:currentGate.state = 'Running'
    Save-Report
    & $Action
    $script:retainedOutputs = Get-NativeCIRetainedOutputs $out -Clock $script:invocationClock
    $script:currentGate.state = 'Passed'
    $script:currentGate.detail = 'Executed successfully; see command logs/input hashes.'
    Save-Report
}
function Run([string]$Executable, [string[]]$Arguments, [string]$Directory = $workspace) {
    $identity = Get-NativeCICommandIdentity $script:currentGate.name $Executable $Arguments $Directory $processEnvironment
    if ($CheckpointWorker -and $script:commandCursor -lt $commands.Count) {
        $previous = $commands[$script:commandCursor]
        if ($previous.identity -ne $identity -or $previous.state -ne 'Passed') { throw 'Checkpoint command identity/order changed; no replay/retry allowed.' }
        $script:commandCursor++
        return $previous.logPrefix
    }
    if ($CheckpointWorker -and $script:newCommands -ge 1) { throw (New-NativeCIBoundary) }
    $number = $commands.Count + 1
    $prefix = Join-Path $out ('logs/{0:D3}-{1}' -f $number, $script:currentGate.name)
    $entry = [pscustomobject]@{ gate = $script:currentGate.name; executable = $Executable; arguments = $Arguments; identity = $identity; state = 'Running'; logPrefix = $prefix; logHashes = @{} }
    $commands.Add($entry)
    Save-Report
    try {
        $null = Invoke-NativeCIProcess -FilePath $Executable -Arguments $Arguments -WorkingDirectory $Directory -LogPrefix $prefix -Environment $processEnvironment
        foreach ($stream in @('stdout','stderr')) { $entry.logHashes[$stream] = (Get-FileHash -LiteralPath "$prefix.$stream.log" -Algorithm SHA256).Hash }
        $script:retainedOutputs = Get-NativeCIRetainedOutputs $out -Clock $script:invocationClock
        $entry.state = 'Passed'
        $script:newCommands++; $script:commandCursor++
    }
    catch { $entry.state = 'Failed'; throw }
    finally { Save-Report }
    return $prefix
}
function Dotnet([string[]]$Arguments, [string]$Directory = (Join-Path $workspace 'desktop')) {
    return Run $script:dotnet $Arguments $Directory
}
function Assert-LocksUnchanged {
    foreach ($inputFile in $inputs) {
        $original = Join-Path $repository $inputFile.path
        $copy = Join-Path $workspace $inputFile.path
        if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $inputFile.sha256 -or
            (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $inputFile.sha256) {
            throw "Frozen input changed: $($inputFile.path)"
        }
    }
}
Save-Report
if ($Mode -eq 'Plan') {
    Write-Output "Plan only: required gates NotRun, exit 2, no build success claimed. $out"
    exit 2
}
try {
    Invoke-Gate 'Inputs' {
        if (-not $IsWindows) { throw 'Native CI requires Windows x64.' }
        if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'Native CI requires x64; no emulation fallback.' }
        if ($DependencyMode -eq 'PublicHosted' -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
            throw 'PublicHosted provisioning is restricted to an ephemeral GitHub-hosted runner.'
        }
        if (-not $NuGetPackageRoot -or -not $CargoCacheRoot) { throw 'Supply explicit public-only NuGetPackageRoot and CargoCacheRoot; private caches are never discovered.' }
        foreach ($path in @($NuGetPackageRoot, $CargoCacheRoot)) {
            if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Cache paths must be absolute.' }
            $null = Assert-NativeCIPlainPath $path
            if ($DependencyMode -eq 'Offline' -and -not (Test-Path -LiteralPath $path -PathType Container)) { throw 'Offline public cache is missing; no download fallback.' }
        }
        if (-not $env:DOTNET_CLI_HOME -or -not (Test-Path -LiteralPath $env:DOTNET_CLI_HOME -PathType Container)) {
            throw 'An existing explicit DOTNET_CLI_HOME is required; do not discover or replace the user home.'
        }
        $null = Assert-NativeCIPlainPath $env:DOTNET_CLI_HOME
        if (-not $CheckpointWorker) { throw 'Every Build invocation, including PublicHosted, must use Invoke-NativeCICheckpoint.ps1 for the whole-invocation deadline.' }
        $null = New-Item -ItemType Directory -Path $workspace
        foreach ($tree in @('desktop','inference','native/winsight-tokenizer')) {
            $source = Assert-NativeCIPlainPath (Join-Path $repository $tree)
            # The copy keeps hard-coded native target/FakeHost layouts valid without touching original build trees.
            $pending = [Collections.Generic.Queue[string]]::new()
            $pending.Enqueue($source)
            while ($pending.Count -gt 0) {
                $directory = $pending.Dequeue()
                foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
                    if ($item.Name -in @('bin','obj','target','.git','models','.artifacts')) { continue }
                    $null = Assert-NativeCIPlainPath $item.FullName
                    if ($item.PSIsContainer) { $pending.Enqueue($item.FullName); continue }
                    $relative = [IO.Path]::GetRelativePath($repository, $item.FullName)
                    $destination = Join-Path $workspace $relative
                    $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
                    Copy-Item -LiteralPath $item.FullName -Destination $destination
                    $inputs.Add([pscustomobject]@{ path = $relative.Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash })
                }
            }
        }
        foreach ($path in @('desktop/src/WinSight.App/packages.lock.json','desktop/src/WinSight.Cli/packages.lock.json','inference/WinSight.InferenceHost/packages.lock.json','native/winsight-tokenizer/Cargo.lock','desktop/tests/WinSight.MediaBackend.Tests/packages.lock.json','desktop/tests/WinSight.MediaProcess.Tests/packages.lock.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $workspace $path))) { throw "Required frozen lock missing: $path" }
        }
        foreach ($sdkPath in @('desktop/global.json','inference/global.json')) {
            $sdk = Get-Content -LiteralPath (Join-Path $workspace $sdkPath) -Raw | ConvertFrom-Json
            if ($sdk.sdk.version -ne '10.0.113') { throw "Unexpected SDK pin in $sdkPath" }
        }
        $script:emptyFeed = Join-Path $out 'empty-feed'
        $null = New-Item -ItemType Directory -Path $emptyFeed
        $sourceUri = if ($DependencyMode -eq 'Offline') { $emptyFeed } else { 'https://api.nuget.org/v3/index.json' }
        $escaped = [Security.SecurityElement]::Escape($sourceUri)
        $script:nugetConfig = Join-Path $out 'NuGet.Config'
        "<configuration><packageSources><clear/><add key=`"public-task-source`" value=`"$escaped`"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" | Set-Content -LiteralPath $nugetConfig -Encoding utf8
        Assert-LocksUnchanged
    }
    Assert-LocksUnchanged
    $script:dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $script:cargo = (Get-Command cargo -CommandType Application -ErrorAction Stop).Source
    Invoke-Gate 'Toolchain' {
        foreach ($directory in @('desktop','inference')) {
            $log = Dotnet @('--version') (Join-Path $workspace $directory)
            if ([IO.File]::ReadAllText("$log.stdout.log").Trim() -ne '10.0.113') { throw 'Exact SDK 10.0.113 was not selected; no install/version fallback.' }
        }
        $null = Dotnet @('--info')
        $null = Run $cargo @('--version')
        $rustc = (Get-Command rustc -CommandType Application -ErrorAction Stop).Source
        $null = Run $rustc @('--version','--verbose')
    }
    $native = Join-Path $workspace 'native/winsight-tokenizer'
    $cargoArgs = @('--manifest-path',(Join-Path $native 'Cargo.toml'),'--target','x86_64-pc-windows-msvc','--locked')
    if ($DependencyMode -eq 'Offline') { $cargoArgs += '--offline' }
    Invoke-Gate 'TokenizerBuild' {
        $null = Run $cargo (@('build','--release') + $cargoArgs) $native
        if (-not (Test-Path -LiteralPath (Join-Path $native 'target/x86_64-pc-windows-msvc/release/winsight_tokenizer.dll'))) { throw 'Native tokenizer DLL missing.' }
    }
    Invoke-Gate 'TokenizerSyntheticTests' {
        # Only these two filters are model-free. The pinned-tokenizer test belongs to ModelParity, never silently skipped as passed.
        foreach ($test in @('tests::null_and_invalid_json_are_errors','tests::panic_becomes_error')) {
            $log = Run $cargo (@('test','--lib') + $cargoArgs + @($test,'--','--exact')) $native
            if ([IO.File]::ReadAllText("$log.stdout.log") -notmatch '1 passed; 0 failed') { throw "Rust synthetic test was not executed: $test" }
        }
    }
    $production = @(
        @{ project = 'src/WinSight.App/WinSight.App.csproj'; directory = 'desktop'; output = 'app' },
        @{ project = 'src/WinSight.Cli/WinSight.Cli.csproj'; directory = 'desktop'; output = 'cli' },
        @{ project = 'WinSight.InferenceHost/WinSight.InferenceHost.csproj'; directory = 'inference'; output = 'inference' }
    )
    Invoke-Gate 'ProductionRestore' {
        foreach ($item in $production) {
            $null = Dotnet @('restore',$item.project,'--runtime','win-x64','--locked-mode','--configfile',$nugetConfig,'--packages',$NuGetPackageRoot,'-p:SelfContained=true') (Join-Path $workspace $item.directory)
        }
        Assert-LocksUnchanged
    }
    Invoke-Gate 'ProductionPublish' {
        foreach ($item in $production) {
            $null = Dotnet @('publish',$item.project,'--no-restore','--configuration','Release','--runtime','win-x64','--self-contained','true','-p:WindowsAppSDKSelfContained=true','-p:PublishSingleFile=false','--output',(Join-Path $out "candidate/$($item.output)")) (Join-Path $workspace $item.directory)
        }
        Copy-Item -LiteralPath (Join-Path $workspace 'desktop/THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $out 'candidate/THIRD-PARTY-NOTICES.txt')
        Copy-Item -LiteralPath (Join-Path $workspace 'desktop/licenses') -Destination (Join-Path $out 'candidate/licenses') -Recurse
        Assert-LocksUnchanged
    }
    $suites = @('WinSight.Storage.Tests','WinSight.Indexing.Tests','WinSight.Inference.Tests','WinSight.Runtime.Tests','WinSight.Pause.Tests')
    Invoke-Gate 'SyntheticRestore' {
        foreach ($suite in $suites) {
            $project = "tests/$suite/$suite.csproj"
            if ($suite -eq 'WinSight.Inference.Tests') {
                foreach ($relative in @($project,'tests/WinSight.Inference.Tests/FakeHost/FakeHost.csproj')) {
                    [xml]$xml = Get-Content -LiteralPath (Join-Path $workspace "desktop/$relative") -Raw
                    if ($xml.SelectNodes('//PackageReference').Count -ne 0) { throw 'Previously package-free inference suite acquired dependencies; a reviewed lock is required.' }
                }
                $null = Dotnet @('restore',$project,'--source',$emptyFeed,'--packages',$NuGetPackageRoot)
            }
            else {
                if (-not (Test-Path -LiteralPath (Join-Path $workspace "desktop/tests/$suite/packages.lock.json"))) { throw "Frozen suite lock missing: $suite" }
                $null = Dotnet @('restore',$project,'--locked-mode','--configfile',$nugetConfig,'--packages',$NuGetPackageRoot)
            }
        }
        Assert-LocksUnchanged
    }
    Invoke-Gate 'SyntheticTests' {
        # Inference Program.cs resolves FakeHost/bin/Debug/net10.0 explicitly, so do not relocate these mirrored outputs or change configuration.
        foreach ($suite in $suites) {
            $project = "tests/$suite/$suite.csproj"
            $null = Dotnet @('build',$project,'--no-restore','--configuration','Debug')
            $arguments = @('run','--project',$project,'--no-build','--no-restore','--configuration','Debug')
            if ($suite -eq 'WinSight.Pause.Tests') {
                $fake = Join-Path $workspace 'desktop/tests/WinSight.Inference.Tests/FakeHost/bin/Debug/net10.0/FakeHost.exe'
                if (-not (Test-Path -LiteralPath $fake)) { throw 'Explicit test-only FakeHost is missing.' }
                $arguments += @('--',$fake,(Join-Path $out 'fixtures/pause'))
            }
            $null = Dotnet $arguments
        }
    }
    $mediaSuites = @('WinSight.NativeMedia.Tests','WinSight.MediaProcess.Tests','WinSight.MediaMetadata.Tests','WinSight.MediaIndexing.Tests','WinSight.MediaBackend.Tests')
    $mediaLockTargets = '-p:CustomAfterMicrosoftCommonTargets=' + (Join-Path $PSScriptRoot 'native-ci/MediaSuiteLocks.targets')
    $actualInfrastructure = Join-Path $out 'candidate/cli/WinSight.Infrastructure.dll'
    $actualCore = Join-Path $out 'candidate/cli/WinSight.Core.dll'
    Invoke-Gate 'MediaSyntheticRestore' {
        # Parent must supply the two reviewed new locks before this required gate; never generate them in CI.
        foreach ($suite in @('WinSight.MediaBackend.Tests','WinSight.MediaProcess.Tests')) {
            if (-not (Test-Path -LiteralPath (Join-Path $workspace "desktop/tests/$suite/packages.lock.json"))) { throw "Required media lock missing: desktop/tests/$suite/packages.lock.json; parent metadata approval/validation required." }
        }
        foreach ($suite in $mediaSuites) {
            $project = "tests/$suite/$suite.csproj"
            if ($suite -in @('WinSight.MediaBackend.Tests','WinSight.MediaProcess.Tests')) {
                $null = Dotnet @('restore',$project,'--runtime','win-x64','--locked-mode','--configfile',$nugetConfig,'--packages',$NuGetPackageRoot,$mediaLockTargets)
            }
            else {
                [xml]$xml = Get-Content -LiteralPath (Join-Path $workspace "desktop/$project") -Raw
                if ($xml.SelectNodes('//PackageReference').Count) { throw "Previously package-free media suite acquired packages: $suite; frozen lock required." }
                # The empty source also applies to implicit framework/runtime packs: a missing installed/cached pack fails, not a download fallback.
                $null = Dotnet @('restore',$project,'--runtime','win-x64','--source',$emptyFeed,'--packages',$NuGetPackageRoot)
            }
        }
        Assert-LocksUnchanged
    }
    Invoke-Gate 'MediaSyntheticTests' {
        foreach ($suite in $mediaSuites) {
            $project = "tests/$suite/$suite.csproj"
            $properties = @($mediaLockTargets,"-p:ActualInfrastructureAssembly=$actualInfrastructure","-p:ActualCoreAssembly=$actualCore")
            $null = Dotnet (@('build',$project,'--runtime','win-x64','--no-restore','--configuration','Debug') + $properties)
            $fixtureRoot = Join-Path $out "fixtures/media-$suite"
            $hostExe = Join-Path $out 'candidate/inference/WinSight.InferenceHost.exe'
            $options = @('--artifact-root',$fixtureRoot)
            $fakeRelative = switch ($suite) {
                'WinSight.MediaProcess.Tests' { 'FakeMediaHost/FakeMediaHost' }
                'WinSight.MediaMetadata.Tests' { 'FakeMediaOpsHost/FakeMediaOpsHost' }
                'WinSight.MediaIndexing.Tests' { 'FakeNativeWorker/FakeNativeWorker' }
                'WinSight.MediaBackend.Tests' { 'CompositeHost/CompositeHost' }
            }
            if ($suite -eq 'WinSight.NativeMedia.Tests') { $options += @('--inference-host',$hostExe) }
            else {
                $childDirectory,$childName = $fakeRelative.Split('/')
                [xml]$child = Get-Content -LiteralPath (Join-Path $workspace "desktop/tests/$suite/$fakeRelative.csproj") -Raw
                $tfm = $child.SelectSingleNode('//TargetFramework').InnerText
                $fakeExe = Join-Path $workspace "desktop/tests/$suite/$childDirectory/bin/Debug/$tfm/win-x64/$childName.exe"
                if (-not (Test-Path -LiteralPath $fakeExe)) { throw "Built explicit media fake missing: $fakeExe" }
                $options += @('--fake-host',$fakeExe)
                if ($suite -eq 'WinSight.MediaProcess.Tests') { $options += @('--inference-host',$hostExe) }
                else { $options += @('--actual-host',$hostExe) }
                if ($suite -eq 'WinSight.MediaMetadata.Tests') { $options += @('--client-assembly',$actualInfrastructure,'--require-integration','true') }
                if ($suite -eq 'WinSight.MediaIndexing.Tests') { $options += @('--actual-infrastructure-assembly',$actualInfrastructure,'--require-integration','true') }
            }
            $null = Dotnet (@('run','--project',$project,'--runtime','win-x64','--no-build','--no-restore','--configuration','Debug') + $properties + @('--') + $options)
            $receipt = Get-Content -LiteralPath (Join-Path $fixtureRoot 'results.json') -Raw | ConvertFrom-Json
            $rows = if ($suite -in @('WinSight.MediaProcess.Tests','WinSight.MediaMetadata.Tests')) { @($receipt) } else { @($receipt.results) }
            $notPassed = @($rows | Where-Object { $_.status -ne 'passed' })
            if ($suite -eq 'WinSight.MediaMetadata.Tests') {
                # Existing suite explicitly records the one physical multi-track fixture limitation as nonblocking, never as a passing case.
                $notPassed = @($notPassed | Where-Object { $_.Name -ne 'host/real-multitrack' -or $_.Status -ne 'pending' -or $_.Blocking -ne $false })
            }
            if ($rows.Count -eq 0 -or $notPassed.Count) { throw "Required media receipt has failed/pending/not-run cases: $suite" }
        }
    }
    Invoke-Gate 'ArtifactInventory' {
        Assert-LocksUnchanged
        foreach ($requiredFile in @('app/WinSight.App.exe','app/WinSight.App.pri','cli/winsight-native.exe','inference/WinSight.InferenceHost.exe','inference/model-manifest.json','inference/runtimes/win-x64/native/winsight_tokenizer.dll','THIRD-PARTY-NOTICES.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path $out "candidate/$requiredFile") -PathType Leaf)) { throw "Candidate payload missing: $requiredFile" }
        }
        if (@(Get-ChildItem -LiteralPath (Join-Path $out 'candidate/app') -Filter '*.xbf' -Recurse -File).Count -eq 0) { throw 'Compiled XAML resources missing.' }
        $payloadFiles = @(Get-ChildItem -LiteralPath (Join-Path $out 'candidate') -Recurse -File)
        if (@($payloadFiles | Where-Object { $_.Extension -in @('.onnx','.safetensors','.gguf') -or $_.FullName -match '[\\/]models[\\/]' }).Count -gt 0) {
            throw 'Model weights/directories found in a model-free candidate; refuse artifact approval.'
        }
        $files = @($payloadFiles | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{ path = [IO.Path]::GetRelativePath((Join-Path $out 'candidate'),$_.FullName).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        [pscustomobject]@{ schema = 1; modelFree = $true; installerReady = $false; releaseApproved = $false; files = $files } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'candidate-inventory.json') -Encoding utf8
    }
    if (@($gates | Where-Object { $_.requiredForCI -and $_.state -ne 'Passed' }).Count -ne 0) { throw 'Required CI gate not passed.' }
    $state = 'passed'
    Save-Report
    Write-Output "Native model-free CI gates passed; full release gates remain unverified. $out"
}
catch {
    if ($CheckpointWorker -and $_.Exception.Data['NativeCICheckpoint']) {
        $state = 'incomplete'; $script:currentGate.state = 'Pending'; $script:currentGate.detail = 'Invocation boundary; completed commands retained, remaining work NotRun.'
        Save-Report
        Write-Output 'Checkpoint incomplete; exit 2 is not CI success. Resume with identical source/parameters.'
        exit 2
    }
    $state = 'failed'
    if ($script:currentGate) { $script:currentGate.state = 'Failed'; $script:currentGate.detail = $_.Exception.Message }
    foreach ($gate in $gates | Where-Object { $_.requiredForCI -and $_.state -eq 'NotRun' }) {
        $gate.state = 'Blocked'; $gate.detail = 'A preceding required gate failed; not executed.'
    }
    Save-Report
    Write-Error -ErrorAction Continue $_
    exit 1
}
