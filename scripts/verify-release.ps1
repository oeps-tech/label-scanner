param([string]$Version='0.1.0',[Parameter(Mandatory)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo 'artifacts'
$zipPath = Join-Path $output "OEPS.Scanner-$Version-win-x64.zip"
$msiPath = Join-Path $output "OEPS.Scanner-$Version-setup-win-x64.msi"
$app = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$sdkPath = Join-Path $output "OEPS.Scanner.Client-$Version.zip"
foreach ($file in @($zipPath,$msiPath,$sdkPath)) {
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = (Get-Content -LiteralPath ($file + '.sha256') -Raw).Trim() -split '\s+',2
    if ($sidecar[0] -ne $actual -or $sidecar[1] -ne [IO.Path]::GetFileName($file)) { throw "Release checksum mismatch: $file" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($archive.Entries | Where-Object { $_.FullName -match '/site-packages/(torch|transformers|tensorflow)' }) { throw 'Release contains legacy OCR libraries.' }
    foreach ($name in @('Scanner.Server.dll','Scanner.TestClient.dll','Scanner.Core.dll','Scanner.Contracts.dll','Scanner.Client.dll','Scanner.Desktop.dll','Scanner.Updates.dll','docs/verification-results.md','worker/recognition/worker.py')) {
        $entry = $archive.GetEntry('app/' + $name)
        if (-not $entry) { throw "Release archive misses $name" }
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $zipHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $app $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($zipHash -ne $sourceHash) { throw "Archive contains stale $name" }
    }
} finally { $archive.Dispose() }
$sdkArchive = [IO.Compression.ZipFile]::OpenRead($sdkPath)
try {
    foreach ($name in @('Scanner.Client.dll','Scanner.Contracts.dll','Scanner.Client.xml','Scanner.Contracts.xml','README.md','Example.cs')) {
        if (-not $sdkArchive.GetEntry($name)) { throw "Client SDK archive misses $name" }
    }
} finally { $sdkArchive.Dispose() }
$python = Join-Path $repo '.tools\python\python.exe'
$msiJson = & $python (Join-Path $repo 'scripts\verify-msi.py') $msiPath $zipPath $Version
if ($LASTEXITCODE -ne 0) { throw 'MSI database validation failed.' }
$msi = $msiJson | ConvertFrom-Json
if ($msi.result -ne 'PASS') { throw 'MSI validation did not return a passing result.' }
$report = [ordered]@{ result='PASS'; version=$Version; verified_at=[DateTime]::UtcNow.ToString('o'); release_sha256_match=$true; archive_matches_published_sources=$true; msi_scope='per-user'; shortcuts=$msi.shortcuts.Count; msi_files=$msi.file_count; installer_matches_zip_size=$msi.application_zip_size_matches; note='MSI inspected read-only; clean-machine installation remains untested.' }
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release-verification.json') -Encoding UTF8
Write-Output 'PASS: release SHA-256 files, final archive sources, MSI scope/version/shortcuts and bundled runtime contents.'
