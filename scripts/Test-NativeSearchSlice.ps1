[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root '.dev/native-migration/release/app'
$fake = Join-Path $root 'desktop/tests/WinSight.Inference.Tests/FakeHost/bin/Debug/net11.0/FakeHost.exe'
if (-not (Test-Path $fake)) { throw 'Build the explicitly test-only fake host first; no real-model fallback is allowed.' }
$fixture = Join-Path $root ('.dev/native-migration/search-slice-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $fixture 'data'
$documents = Join-Path $fixture 'documents'
$models = Join-Path $fixture 'success models'
$logs = Join-Path $root '.dev/native-migration/verification'
New-Item -ItemType Directory -Path $documents,$models -Force | Out-Null
'# Smoke test: this is synthetic fixture text, not personal data.' | Set-Content (Join-Path $documents 'smoke-test.md') -Encoding utf8
$manifest = Join-Path $models 'manifest with spaces &.json'
'{}' | Set-Content $manifest -Encoding utf8
$cli = Join-Path $app 'winsight-native.exe'
& $cli configure --data-dir $data --root $documents --host $fake --models $models --manifest $manifest *> (Join-Path $logs 'search-slice.configure.log')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic search-slice configure failed.' }
& $cli index --data-dir $data --root $documents *> (Join-Path $logs 'search-slice.index.log')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic search-slice indexing failed.' }
& $cli search --data-dir $data --query smoke-test --mode keyword *> (Join-Path $logs 'search-slice.keyword.json')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic search-slice keyword query failed.' }
$result = Get-Content (Join-Path $logs 'search-slice.keyword.json') -Raw | ConvertFrom-Json
if (@($result.hits).Count -ne 1 -or $result.hits[0].path -ne (Join-Path $documents 'smoke-test.md')) { throw 'Actual keyword result did not identify the synthetic file.' }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'WinSight.App.exe'))
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--DATA-DIR')
$start.ArgumentList.Add($data)
$start.ArgumentList.Add('--smoke-test')
$process = [Diagnostics.Process]::Start($start)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw 'Search-slice App timed out; owned test tree terminated.' }
    $text = $stdout.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $logs 'search-slice.app.stdout.log'), $text)
    [IO.File]::WriteAllText((Join-Path $logs 'search-slice.app.stderr.log'), $stderr.GetAwaiter().GetResult())
    if ($process.ExitCode -ne 0 -or $text -notmatch 'Search hits:\s+([1-9][0-9]*)') { throw "Actual App failed nonempty synthetic search: exit=$($process.ExitCode)." }
    [pscustomobject]@{ actualCliIndexedSyntheticDocument = $true; actualKeywordHits = @($result.hits).Count;
        actualAppExit = $process.ExitCode; actualAppSearchHits = [int]$Matches[1];
        model = 'explicit test-only fake host'; realModelValidated = $false;
        pixelsOrFileOpeningValidated = $false; fixture = $fixture; releaseApproved = $false
    } | ConvertTo-Json | Set-Content (Join-Path $logs 'search-slice.result.json') -Encoding utf8
}
finally { $process.Dispose() }
Write-Output 'Synthetic nonempty CLI-index -> SQLite/search -> actual native App slice passed; model/visual/file-opening parity is not approved.'
