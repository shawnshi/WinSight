#requires -Version 7.0
<#
.SYNOPSIS
Builds the per-machine WinSight installers (one per CPU vendor) from a staged payload (see Prepare-NativeRelease.ps1).

.DESCRIPTION
Canonical mainline installer builder for WinSight.
Uses desktop/installer/WinSight.iss: each installer requires elevation and installs into
C:\Program Files\WinSight ({autopf64}) with direct Exe shortcuts, optional PATH task, full process
termination handling, and uninstallation cleanup.
One installer is produced per CPU vendor (-Vendor amd, intel): WinSight-<version>-x64-<vendor>-setup.exe
carries the common payload plus only that vendor's model directory (models\RyzenAI or
models\IntelOpenVINO), and refuses to run on the other vendor's CPU.
The payload uses one shared private .NET runtime (app\runtime) for every host. The [Files] list is
generated per file and content-identical files are stored once. [InstallDelete] removes the per-host
.NET runtime copies left behind by earlier self-contained releases when upgrading in place.
The product version comes from desktop/Directory.Build.props.
Optional Authenticode signing runs Sign-NativeArtifacts.ps1 on the payload binaries first and then
signs each installer executable through an Inno Setup "SignTool" definition.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageDirectory,
    [ValidateSet('all', 'amd', 'intel')][string[]]$Vendor = @('all'),
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$CertificateThumbprint,
    [string]$PfxPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$Vendor = @($Vendor | Sort-Object -Unique)

$payload = Join-Path $StageDirectory 'app'
$allRequired = @(
    'WinSight.App.exe',
    'winsight-native.exe',
    'bridge\winsight-mcp.exe',
    'inference\WinSight.InferenceHost.exe',
    'inference\ocr\WinSight.OcrHost.exe'
)
foreach ($required in $allRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
        throw "Payload is incomplete, missing required host: $required"
    }
}
if (-not (Get-ChildItem -LiteralPath $payload -Recurse -Filter winsight_segmenter.dll -File)) {
    throw 'Payload lacks winsight_segmenter.dll; indexing would fail at run time.'
}
$runtimeDir = Join-Path $payload 'runtime'
if (-not (Test-Path -LiteralPath (Join-Path $runtimeDir 'host\fxr') -PathType Container) -or
    -not (Get-ChildItem -LiteralPath (Join-Path $runtimeDir 'host\fxr') -Recurse -Filter hostfxr.dll -File) -or
    -not (Get-ChildItem -LiteralPath (Join-Path $runtimeDir 'shared\Microsoft.NETCore.App') -Recurse -Filter coreclr.dll -File -ErrorAction SilentlyContinue)) {
    throw 'Payload lacks the shared .NET runtime (app\runtime\host\fxr and shared\Microsoft.NETCore.App); re-run Prepare-NativeRelease.ps1.'
}
$hostDirs = @(Get-ChildItem -LiteralPath $payload -Recurse -Filter '*.runtimeconfig.json' -File |
    Where-Object { $_.FullName -notlike "$runtimeDir*" } |
    ForEach-Object { $_.DirectoryName } | Sort-Object -Unique)
foreach ($hostDir in $hostDirs) {
    if (Test-Path -LiteralPath (Join-Path $hostDir 'coreclr.dll')) {
        throw "Host directory still carries its own .NET runtime (self-contained publish): $hostDir; re-run Prepare-NativeRelease.ps1."
    }
}
$modelDirs = @{ amd = 'models\RyzenAI'; intel = 'models\IntelOpenVINO' }
$vendorsToCheck = if ($Vendor -contains 'all') { @('amd', 'intel') } else { $Vendor }
foreach ($v in $vendorsToCheck) {
    $dir = Join-Path $payload $modelDirs[$v]
    if (-not (Test-Path -LiteralPath $dir -PathType Container) -or -not (Get-ChildItem -LiteralPath $dir -Recurse -File)) {
        throw "Payload lacks $($modelDirs[$v]); the installer would ship without its models (stage with -IncludePlatformModels)."
    }
}
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
if (Get-ChildItem -LiteralPath $payload -Recurse -Directory | Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) }) {
    throw 'Payload contains empty directories; the generated per-file [Files] list would not create them.'
}

# Inno Setup merges entries only when they share a Source path, never by content, so every copy of an
# identical file points at one canonical source.
$payloadRoot = [IO.Path]::GetFullPath($payload).TrimEnd('\')
$records = foreach ($file in $payloadFiles) {
    $rel = $file.FullName.Substring($payloadRoot.Length + 1)
    # Braces are Inno constants in DestDir/DestName and ISPP inline syntax; refuse rather than mis-escape.
    if ($rel.IndexOfAny([char[]]'{}') -ge 0) { throw "Payload path contains '{' or '}': $rel" }
    $owner = if ($rel -like "$($modelDirs.amd)\*") { 'amd' } elseif ($rel -like "$($modelDirs.intel)\*") { 'intel' } else { 'common' }
    [pscustomobject]@{ Rel = $rel; Owner = $owner; Length = $file.Length; Extension = $file.Extension.ToLowerInvariant(); Name = $file.Name.ToLowerInvariant(); Key = $null }
}
# Only files whose size collides can be duplicates; hash just those.
foreach ($sizeGroup in $records | Group-Object Length) {
    foreach ($r in $sizeGroup.Group) {
        $r.Key = if ($sizeGroup.Count -eq 1) { "unique:$($r.Rel)" }
                 else { (Get-FileHash -LiteralPath (Join-Path $payloadRoot $r.Rel) -Algorithm SHA256).Hash }
    }
}

# Upgrades over earlier releases: Inno never deletes files that the new version no longer
# ships, so remove the retired winml host and every .NET runtime file name from each host directory.
$payloadRelSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($r in $records) { [void]$payloadRelSet.Add($r.Rel) }
$runtimeNames = @(Get-ChildItem -LiteralPath $runtimeDir -Recurse -File | ForEach-Object Name | Sort-Object -Unique)
$installDelete = [Collections.Generic.List[string]]::new()
$installDelete.Add('Type: filesandordirs; Name: "{app}\inference\winml"')
foreach ($hostDir in $hostDirs) {
    $relDir = if ($hostDir.Length -gt $payloadRoot.Length) { $hostDir.Substring($payloadRoot.Length + 1) } else { '' }
    foreach ($name in $runtimeNames) {
        $rel = if ($relDir) { "$relDir\$name" } else { $name }
        if (-not $payloadRelSet.Contains($rel)) { $installDelete.Add("Type: files; Name: `"{app}\$rel`"") }
    }
}

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'desktop/Directory.Build.props') -Raw
$prefix = $props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
$suffix = $props.Project.PropertyGroup.VersionSuffix | Where-Object { $_ } | Select-Object -First 1
if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw 'VersionPrefix in desktop/Directory.Build.props must be major.minor.patch.' }
$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }

$iscc = $CompilerPath
if (-not $iscc) {
    $candidates = @(
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        "$env:ProgramW6432\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        "$env:ProgramW6432\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Users\shich\AppData\Local\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6/7) was not found; install it or pass -CompilerPath.' }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $StageDirectory 'installer' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Signing rewrites payload binaries; the duplicate map above was taken first, so identical copies still
# collapse to one (signed) canonical source.
$signing = $CertificateThumbprint -or $PfxPath
if ($signing) {
    & (Join-Path $PSScriptRoot 'Sign-NativeArtifacts.ps1') -Path $payload -CertificateThumbprint $CertificateThumbprint -PfxPath $PfxPath
}

$issFile = Join-Path $root 'desktop/installer/WinSight.iss'
if (-not (Test-Path -LiteralPath $issFile)) { $issFile = Join-Path $root 'desktop/installer/WinSight.Simple.iss' }

foreach ($v in $Vendor) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("; Generated by scripts/Build-NativeInstaller.ps1 for vendor/mode '$v'; do not edit.")
    $lines.Add('[InstallDelete]')
    foreach ($entry in $installDelete) { $lines.Add($entry) }
    $lines.Add('[Files]')

    $selected = @()
    $storedBytes = 0
    $installedBytes = 0

    if ($v -eq 'all') {
        # Shape B unified installer: stores common, shared, amd and intel streams with solidbreak so each CPU vendor skips the other stream.
        $selected = $records
        $streamOrder = @('common', 'shared', 'amd', 'intel')
        $contents = foreach ($g in $records | Group-Object Key) {
            $copies = @($g.Group | Sort-Object Rel)
            $owners = @($copies.Owner | Sort-Object -Unique)
            $stream = if ($owners -contains 'common') { 'common' }
                      elseif ($owners.Count -gt 1) { 'shared' }
                      else { $owners[0] }
            [pscustomobject]@{
                Stream = $stream; Order = $streamOrder.IndexOf($stream);
                Extension = $copies[0].Extension; Name = $copies[0].Name; Copies = $copies
            }
        }
        $checks = @{ common = $null; amd = 'IsAmdCpu'; intel = 'IsIntelCpu' }
        $currentStream = $null
        foreach ($c in $contents | Sort-Object Order, Extension, Name, { $_.Copies[0].Rel }) {
            $source = Join-Path $payloadRoot $c.Copies[0].Rel
            foreach ($copy in $c.Copies) {
                $dir = Split-Path $copy.Rel -Parent
                $destDir = if ($dir) { "{app}\$dir" } else { '{app}' }
                $flags = 'ignoreversion'
                if ($c.Stream -ne $currentStream) { $flags += ' solidbreak'; $currentStream = $c.Stream }
                $line = "Source: `"$source`"; DestDir: `"$destDir`"; DestName: `"$(Split-Path $copy.Rel -Leaf)`""
                if ($checks[$copy.Owner]) { $line += "; Check: $($checks[$copy.Owner])" }
                $lines.Add("$line; Flags: $flags")
            }
        }
        $storedBytes = ($contents | ForEach-Object { $_.Copies[0].Length } | Measure-Object -Sum).Sum
    } else {
        # Single-vendor installer: only common and this vendor's models are included.
        $selected = @($records | Where-Object { $_.Owner -eq 'common' -or $_.Owner -eq $v })
        $contents = foreach ($g in $selected | Group-Object Key) {
            $copies = @($g.Group | Sort-Object Rel)
            [pscustomobject]@{ Extension = $copies[0].Extension; Name = $copies[0].Name; Copies = $copies }
        }
        foreach ($c in $contents | Sort-Object Extension, Name, { $_.Copies[0].Rel }) {
            $source = Join-Path $payloadRoot $c.Copies[0].Rel
            foreach ($copy in $c.Copies) {
                $dir = Split-Path $copy.Rel -Parent
                $destDir = if ($dir) { "{app}\$dir" } else { '{app}' }
                $lines.Add("Source: `"$source`"; DestDir: `"$destDir`"; DestName: `"$(Split-Path $copy.Rel -Leaf)`"; Flags: ignoreversion")
            }
        }
        $storedBytes = ($contents | ForEach-Object { $_.Copies[0].Length } | Measure-Object -Sum).Sum
    }

    $filesInclude = Join-Path $StageDirectory "WinSight.Files.$v.generated.iss"
    Set-Content -LiteralPath $filesInclude -Value $lines -Encoding utf8BOM

    $installedBytes = ($selected | Measure-Object -Property Length -Sum).Sum
    Write-Output ("[{0}] installs {1:N0} MiB in {2} files; stored once: {3:N0} MiB; stale-runtime deletes: {4}" -f
        $v, ($installedBytes / 1MB), $selected.Count, ($storedBytes / 1MB), @($installDelete).Count)

    $arguments = @(
        "/DPayloadDir=$payload",
        "/DAppVersion=$version",
        "/DNumericVersion=$prefix.0",
        "/DOutputDir=$OutputDirectory",
        "/DFilesInclude=$filesInclude",
        "/DVendor=$v"
    )
    if ($CertificateThumbprint) {
        $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
        if (-not $signtool) { throw 'signtool.exe must be on PATH to sign the installer itself.' }
        $arguments += @('/DSignToolName=winsightsign', "/Swinsightsign=`"$signtool`" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 $CertificateThumbprint `$f")
    }
    & $iscc @arguments $issFile
    if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed for mode '$v' (exit $LASTEXITCODE)." }

    $baseName = if ($v -eq 'all') { "WinSight-$version-x64-setup" } else { "WinSight-$version-x64-$v-setup" }
    $installer = Get-ChildItem -LiteralPath $OutputDirectory -Filter "$baseName.exe" | Select-Object -First 1
    if (-not $installer) { throw "The $v installer was not produced." }
    $hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashContent = "$hash  $($installer.Name)`n"
    Get-ChildItem -LiteralPath $OutputDirectory -Filter "$baseName-*.bin" | ForEach-Object {
        $binHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashContent += "$binHash  $($_.Name)`n"
    }
    Set-Content -LiteralPath ($installer.FullName + '.sha256') -Value $hashContent.TrimEnd() -Encoding ascii
    Write-Output "Built $($installer.FullName) ($([math]::Round($installer.Length / 1MB, 1)) MiB, sha256 $hash, installs $([math]::Ceiling($installedBytes / 1KB)) KB)$(if (-not $signing) { '; UNSIGNED' })"
}
