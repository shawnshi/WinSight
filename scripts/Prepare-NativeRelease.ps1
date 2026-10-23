[CmdletBinding()]
param(
    [switch]$ReuseFrozenPayload,
    [string]$FrozenPublishedRoot,
    [string]$FrozenManifestPath,
    [string]$FrozenManifestSha256,
    [string]$ModelManifestPath,
    [string]$ModelManifestSha256,
    [string]$LicenseReportPath,
    [string]$LicenseReportSha256,
    [string]$ArtifactRoot,
    [ValidateRange(1,160)][int]$DeadlineSeconds = 150,
    [switch]$AppOnlyPreview,
    [string]$ModelDirectory,
    [string]$Configuration = 'Release',
    [string]$StageDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$desktop = Join-Path $root 'desktop'

# This branch deliberately returns before any legacy publish/toolchain preflight.
if ($ReuseFrozenPayload) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    function Assert-ReuseTime {
        if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Frozen assembly deadline exceeded; partial artifacts retained.' }
    }
    function Resolve-ReusePath([string]$Path) {
        Assert-ReuseTime
        if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '[\r\n]') { throw 'Reuse paths must be explicit absolute local paths.' }
        foreach ($part in $Path.Substring(3).Replace('/', '\').Split('\')) {
            if (-not $part -or $part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
                $part -match '[<>:"|?*~]' -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'Unsafe reuse path segment.' }
        }
        $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
        for ($p = $full; $p; $p = [IO.Path]::GetDirectoryName($p)) {
            if ([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) {
                if ([IO.File]::GetAttributes($p) -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) { throw "Reparse/device path: $p" }
            }
        }
        $full
    }
    function Assert-ReuseSeparate([string]$Left, [string]$Right) {
        if ($Left.Equals($Right, [StringComparison]::OrdinalIgnoreCase) -or
            $Left.StartsWith($Right + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $Right.StartsWith($Left + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Overlapping reuse source/artifact paths.' }
    }
    function Assert-ReuseRelative([string]$Path) {
        if ($Path -cnotmatch '^app/[A-Za-z0-9_. /-]+$' -or $Path.Contains('//')) { throw "Unsafe recorded path: $Path" }
        foreach ($part in $Path.Split('/')) {
            if ($part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
                $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw "Unsafe recorded segment: $Path" }
        }
    }
    function Get-ReuseHash([string]$Path) {
        $null = Resolve-ReusePath $Path
        $stream = [IO.File]::Open($Path, 'Open', 'Read', 'Read')
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $buffer = New-Object byte[] 1048576
            while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                Assert-ReuseTime
                $null = $sha.TransformBlock($buffer, 0, $n, $buffer, 0)
            }
            $null = $sha.TransformFinalBlock($buffer, 0, 0)
            [BitConverter]::ToString($sha.Hash).Replace('-', '').ToLowerInvariant()
        } finally { $sha.Dispose(); $stream.Dispose() }
    }
    function Read-ReuseControl([string]$Path, [string]$Pin) {
        if ($Pin -cnotmatch '^[0-9a-f]{64}$') { throw 'Lowercase SHA256 control pins are required.' }
        $null = Resolve-ReusePath $Path
        $info = New-Object IO.FileInfo $Path
        if (-not $info.Exists -or $info.Length -gt 2097152) { throw 'Missing/oversized JSON control (maximum 2 MiB).' }
        if ((Get-ReuseHash $Path) -cne $Pin) { throw "Control hash mismatch: $Path" }
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -gt 2097152) { throw 'Growing JSON control.' }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        if ($actual -cne $Pin) { throw "Control source drift: $Path" }
        [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff) | ConvertFrom-Json
    }
    function Get-ReuseTree([string]$Directory) {
        Assert-ReuseTime
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Directory)) {
            $null = Resolve-ReusePath $entry
            $entry
            if ([IO.File]::GetAttributes($entry) -band [IO.FileAttributes]::Directory) { Get-ReuseTree $entry }
        }
    }
    function Assert-ReuseTree([string]$Directory, $Records) {
        $allowed = @{}; $dirs = @{}
        foreach ($record in $Records) {
            $allowed[$record.source] = $record.source
            for ($d = [IO.Path]::GetDirectoryName($record.source); $d.Length -gt $Directory.Length; $d = [IO.Path]::GetDirectoryName($d)) { $dirs[$d] = $true }
        }
        foreach ($entry in @(Get-ReuseTree $Directory)) {
            if ([IO.Directory]::Exists($entry)) {
                if (-not $dirs.ContainsKey($entry)) { throw "Unknown frozen directory: $entry" }
            } elseif (-not $allowed.ContainsKey($entry)) { throw "Unknown frozen file: $entry" }
            elseif ($allowed[$entry] -cne $entry) { throw "Frozen case collision: $entry" }
        }
    }
    function Write-ReuseJson([string]$Path, $Value) {
        Assert-ReuseTime
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 12))
        $s = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
        try { $s.Write($bytes, 0, $bytes.Length); $s.Flush() } finally { $s.Dispose() }
    }
    if ($AppOnlyPreview -or $StageDirectory -or $PSBoundParameters.ContainsKey('Configuration')) { throw 'Reuse cannot be combined with publish/preview options.' }
    $published = Resolve-ReusePath $FrozenPublishedRoot
    $models = Resolve-ReusePath $ModelDirectory
    $artifact = Resolve-ReusePath $ArtifactRoot
    $boundary = Resolve-ReusePath (Join-Path $root '.dev')
    if (-not $artifact.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'New artifact root must be below this checkout .dev.' }
    foreach ($source in @($published, $models, (Resolve-ReusePath $FrozenManifestPath), (Resolve-ReusePath $ModelManifestPath), (Resolve-ReusePath $LicenseReportPath), $desktop)) {
        Assert-ReuseSeparate $artifact $source
    }
    Assert-ReuseSeparate $published $models
    if ([IO.Directory]::Exists($artifact) -or [IO.File]::Exists($artifact)) { throw 'Refusing existing artifact root.' }
    $frozen = Read-ReuseControl $FrozenManifestPath $FrozenManifestSha256
    $model = Read-ReuseControl $ModelManifestPath $ModelManifestSha256
    $licenses = Read-ReuseControl $LicenseReportPath $LicenseReportSha256
    if ($model.repository -isnot [string] -or $model.revision -isnot [string]) { throw 'Model repository and revision must be scalar strings.' }
    if (@($frozen.files).Count -lt 1 -or @($frozen.files).Count -gt 2000 -or @($model.files).Count -ne 11 -or
        $model.repository -cne 'onnx-community/embeddinggemma-2-ONNX' -or $model.revision -cne 'daa72c51243991dfcaf9f9137d2c573d8f7790c0') { throw 'Invalid bounded frozen/model inventory.' }
    $records = New-Object 'Collections.Generic.List[object]'
    $seen = @{}; $segments = @{}
    foreach ($f in $frozen.files) {
        Assert-ReuseRelative $f.path
        if ($f.path.StartsWith('app/models/', [StringComparison]::OrdinalIgnoreCase)) { throw 'Frozen base must not contain model files.' }
        $records.Add([pscustomobject]@{ path = $f.path; bytes = $f.bytes; sha256 = $f.sha256; source = Join-Path $published $f.path.Substring(4); kind = 'published' })
    }
    Assert-ReuseTree $published $records
    # Metadata beside the eleven model files is not a distribution input.
    $null = @(Get-ReuseTree $models)
    foreach ($f in $model.files) {
        Assert-ReuseRelative ('app/' + $f.path)
        $records.Add([pscustomobject]@{ path = 'app/models/' + $f.path; bytes = $f.size; sha256 = $f.sha256; source = Join-Path $models $f.path; kind = 'model' })
    }
    foreach ($relative in @('THIRD-PARTY-NOTICES.txt', 'licenses/DigUp-MIT.txt')) {
        $source = Join-Path $desktop $relative
        $records.Add([pscustomobject]@{ path = 'app/' + $relative; bytes = (New-Object IO.FileInfo $source).Length; sha256 = Get-ReuseHash $source; source = $source; kind = 'notice' })
    }
    $total = [long]0
    foreach ($r in $records) {
        Assert-ReuseRelative $r.path
        if ($seen.ContainsKey($r.path) -or $r.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $r.bytes -isnot [ValueType] -or $r.bytes -lt 0 -or [long]$r.bytes -ne $r.bytes) { throw 'Duplicate/case collision or malformed file record.' }
        $seen[$r.path] = $true
        $prefix = ''
        foreach ($part in $r.path.Split('/')) {
            $prefix += '/' + $part
            if ($segments.ContainsKey($prefix) -and $segments[$prefix] -cne $prefix) { throw 'Case-colliding recorded path segments.' }
            $segments[$prefix] = $prefix
        }
        $null = Resolve-ReusePath $r.source
        if (-not [IO.File]::Exists($r.source) -or (New-Object IO.FileInfo $r.source).Length -ne $r.bytes) { throw "Missing/size mismatch: $($r.path)" }
        $total += $r.bytes
        if ($total -gt 2147483648) { throw 'Payload exceeds 2 GiB assembly bound.' }
    }
    foreach ($required in @('app/WinSight.App.exe', 'app/winsight-native.exe', 'app/inference/WinSight.InferenceHost.exe', 'app/inference/runtimes/win-x64/native/winsight_tokenizer.dll')) {
        if (-not $seen.ContainsKey($required)) { throw "Missing executable payload role: $required" }
    }
    $stage = Join-Path $artifact 'stage'
    $null = [IO.Directory]::CreateDirectory($stage)
    try {
        foreach ($r in $records) {
            Assert-ReuseTime
            $null = Resolve-ReusePath $r.source
            $target = Join-Path $stage $r.path
            $null = Resolve-ReusePath $target
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $inputStream = [IO.File]::Open($r.source, 'Open', 'Read', 'Read')
            $outputStream = $null
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $outputStream = [IO.File]::Open($target, 'CreateNew', 'Write', 'None')
                $buffer = New-Object byte[] 1048576
                $copied = [long]0
                while (($n = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    Assert-ReuseTime
                    $copied += $n
                    if ($copied -gt $r.bytes) { throw "Growing source: $($r.path)" }
                    $null = $sha.TransformBlock($buffer, 0, $n, $buffer, 0)
                    $outputStream.Write($buffer, 0, $n)
                }
                $null = $sha.TransformFinalBlock($buffer, 0, 0)
                $hash = [BitConverter]::ToString($sha.Hash).Replace('-', '').ToLowerInvariant()
                if ($copied -ne $r.bytes -or $hash -cne $r.sha256) { throw "Source hash mismatch: $($r.path)" }
                $outputStream.Flush()
            } finally { if ($outputStream) { $outputStream.Dispose() }; $sha.Dispose(); $inputStream.Dispose() }
            if ((Get-ReuseHash $target) -cne $r.sha256) { throw "Post-copy hash mismatch: $($r.path)" }
        }
        Assert-ReuseTree $published @($records | Where-Object kind -eq 'published')
        foreach ($r in $records) {
            if ((New-Object IO.FileInfo $r.source).Length -ne $r.bytes -or (Get-ReuseHash $r.source) -cne $r.sha256) { throw "Source drift: $($r.path)" }
        }
        $null = Read-ReuseControl $FrozenManifestPath $FrozenManifestSha256
        $null = Read-ReuseControl $ModelManifestPath $ModelManifestSha256
        $null = Read-ReuseControl $LicenseReportPath $LicenseReportSha256
        $files = @($records | ForEach-Object { [ordered]@{ path = $_.path; bytes = [long]$_.bytes; sha256 = $_.sha256 } })
        # Only the helper's closed payload schema belongs in the stage.
        Write-ReuseJson (Join-Path $stage 'release-manifest.json') ([ordered]@{
            schema = 1; runtime = 'win-x64'; selfContained = $true; appOnlyPreview = $false; releaseApproved = $false; files = $files
        })
        Write-ReuseJson (Join-Path $artifact 'assembly-receipt.json') ([ordered]@{
            schema = 1; completeExecutableModelBytes = $true; installerReady = $false; releaseApproved = $false
            stage = $stage; stageManifestSha256 = Get-ReuseHash (Join-Path $stage 'release-manifest.json')
            frozenRoot = $published; frozenManifestPath = $FrozenManifestPath; frozenManifestSha256 = $FrozenManifestSha256
            modelRoot = $models; modelManifestPath = $ModelManifestPath; modelManifestSha256 = $ModelManifestSha256
            repository = $model.repository; revision = $model.revision
            licenseReportPath = $LicenseReportPath; licenseReportSha256 = $LicenseReportSha256
            missingLicenseTexts = @($licenses.packages | Where-Object { -not $_.termsTextCollected } | ForEach-Object { $_.id + '/' + $_.version })
            pending = @('Model distribution permission/text', 'Runtime/native/tokenizer/dictionary/media notice closure', 'Legal review', 'Vulnerability audit', 'Independent review and actual installer gates')
            fileCount = $files.Count; bytes = $total; elapsedSeconds = $clock.Elapsed.TotalSeconds
            verification = 'Every source stream and copied file SHA256 matched; all recorded sources rehashed after copying.'
            files = $files
        })
    } catch {
        # No automatic cleanup or success marker: the exact partial stage is evidence.
        throw
    }
    Write-Output "Reuse-only local candidate (not installer/release approved): $stage"
    return
}
if ($PSBoundParameters.ContainsKey('ReuseFrozenPayload')) {
    throw 'Explicit -ReuseFrozenPayload:$false is rejected; publishing was not selected.'
}
foreach ($reuseParameter in @('FrozenPublishedRoot', 'FrozenManifestPath', 'FrozenManifestSha256',
    'ModelManifestPath', 'ModelManifestSha256', 'LicenseReportPath', 'LicenseReportSha256', 'ArtifactRoot', 'DeadlineSeconds')) {
    if ($PSBoundParameters.ContainsKey($reuseParameter)) {
        throw 'Frozen arguments require explicit enabled -ReuseFrozenPayload; publishing was not selected.'
    }
}
$stage = Join-Path $root '.dev/native-migration/release'
if ($StageDirectory) {
    if (-not [IO.Path]::IsPathFullyQualified($StageDirectory)) { throw '-StageDirectory must be an absolute task directory.' }
    $stage = [IO.Path]::GetFullPath($StageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
}
$boundary = [IO.Path]::GetFullPath((Join-Path $root '.dev/native-migration'))
if (-not $stage.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release staging must remain under the project .dev/native-migration directory.'
}
for ($parent = Split-Path $stage -Parent; $parent.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase); $parent = Split-Path $parent -Parent) {
    if ((Test-Path $parent) -and ((Get-Item $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse-point stage ancestor: $parent" }
}
if (Test-Path $stage) { throw "Refusing to overwrite an existing release stage: $stage" }
$manifestPath = Join-Path $root 'inference/WinSight.InferenceHost/model-manifest.json'
$tokenizer = Join-Path $root 'native/winsight-tokenizer/target/x86_64-pc-windows-msvc/release/winsight_tokenizer.dll'
if (-not $AppOnlyPreview) {
    if (-not $ModelDirectory -or -not [IO.Path]::IsPathFullyQualified($ModelDirectory)) {
        throw 'A full package requires an explicitly supplied absolute -ModelDirectory; no private model cache is discovered.'
    }
    $modelManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $modelManifest.files) {
        $path = Join-Path $ModelDirectory $file.path
        if (-not (Test-Path $path -PathType Leaf)) { throw "Missing model artifact: $($file.path)" }
        $item = Get-Item $path
        if ($item.Length -ne $file.size -or (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Model manifest mismatch: $($file.path)"
        }
    }
    if (-not (Test-Path $tokenizer)) { throw 'Build the pinned native tokenizer before preparing the inference payload.' }
}
New-Item -ItemType Directory -Path $stage | Out-Null
$logs = Join-Path $stage 'logs'
New-Item -ItemType Directory -Path $logs | Out-Null
Push-Location $desktop
try {
    $projects = @(
        @{ Project = 'src/WinSight.App/WinSight.App.csproj'; Output = 'app'; Log = 'app' },
        @{ Project = 'src/WinSight.Cli/WinSight.Cli.csproj'; Output = 'app'; Log = 'cli' }
    )
    foreach ($item in $projects) {
        $out = Join-Path $stage $item.Output
        & dotnet publish $item.Project --configuration $Configuration --runtime win-x64 --self-contained true `
            -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false --output $out *> (Join-Path $logs "$($item.Log).publish.log")
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($item.Project). See stage logs; stage is incomplete." }
    }
    if (-not $AppOnlyPreview) {
        $inference = Join-Path $stage 'app/inference'
        & dotnet publish (Join-Path $root 'inference/WinSight.InferenceHost/WinSight.InferenceHost.csproj') `
            --configuration $Configuration --runtime win-x64 --self-contained true --output $inference *> (Join-Path $logs 'inference.publish.log')
        if ($LASTEXITCODE -ne 0) { throw 'Inference publish failed; stage is incomplete.' }
        foreach ($file in $modelManifest.files) {
            $target = Join-Path (Join-Path $stage 'app/models') $file.path
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item (Join-Path $ModelDirectory $file.path) $target
        }
    }
    Copy-Item (Join-Path $desktop 'THIRD-PARTY-NOTICES.txt') (Join-Path $stage 'app/THIRD-PARTY-NOTICES.txt')
    Copy-Item (Join-Path $desktop 'licenses') (Join-Path $stage 'app/licenses') -Recurse
    $files = @(Get-ChildItem $stage -Recurse -File | Where-Object { $_.FullName -notlike "$logs*" } | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/'); bytes = $_.Length;
           sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [pscustomobject]@{
        schema = 1; product = 'WinSight NativePreview'; runtime = 'win-x64'; selfContained = $true;
        appOnlyPreview = [bool]$AppOnlyPreview; releaseApproved = $false;
        pending = @('Full behavior and model parity', 'Independent review', 'License and vulnerability gate',
            'Installer compilation and clean-machine install/upgrade/uninstall/recovery'); files = $files
    } | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $stage 'release-manifest.json') -Encoding utf8
}
finally { Pop-Location }
Write-Output "Staged candidate only, not approved for distribution: $stage"
