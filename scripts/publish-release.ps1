param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$safeRepo = 'safe.directory=' + $repo.Replace('\','/')
$tag = 'v' + $Version
$notes = Join-Path $repo "docs\releases\$tag.md"
$names = @("OEPS.Scanner-$Version-setup-win-x64.msi", "OEPS.Scanner-$Version-win-x64.zip", "OEPS.Scanner.Client-$Version.zip")
foreach ($name in $names) {
    $path = Join-Path $repo ('artifacts\' + $name)
    $sidecar = (Get-Content -LiteralPath ($path + '.sha256') -Raw).Trim() -split '\s+',2
    if ($sidecar[0] -ne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -or $sidecar[1] -ne $name) { throw "Checksum mismatch: $name" }
}
$names += @($names | ForEach-Object { $_ + '.sha256' })
$names += @("OEPS.Scanner.Client.$Version.nupkg", "OEPS.Scanner.Contracts.$Version.nupkg")
foreach ($name in $names) { if (-not (Test-Path -LiteralPath (Join-Path $repo ('artifacts\' + $name)))) { throw "Missing asset: $name" } }
$verification = Get-Content -LiteralPath (Join-Path $repo 'artifacts\release-verification.json') -Raw | ConvertFrom-Json
if ($verification.result -ne 'PASS' -or $verification.version -ne $Version) { throw 'Run verify-release.ps1 for this version before publication.' }
$body = Get-Content -LiteralPath $notes -Raw -Encoding UTF8
$dirty = & git -C $repo -c $safeRepo status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit all release source changes before publishing.' }
$sha = (& git -C $repo -c $safeRepo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve release commit.' }

# Use the existing Git credential manager sign-in. Never print or persist credentials.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$credentialLines = "protocol=https`nhost=github.com`n`n" | & git -C $repo -c $safeRepo credential fill
if ($LASTEXITCODE -ne 0) { throw 'GitHub sign-in is unavailable.' }
$passwordLine = $credentialLines | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
if (-not $passwordLine) { throw 'GitHub credential missing.' }
$headers = @{ Authorization = 'Bearer ' + $passwordLine.Substring(9); 'User-Agent' = 'OEPS-Scanner-Release'; Accept = 'application/vnd.github+json' }
$base = 'https://api.github.com/repos/oeps-tech/label-scanner'
function Request([string]$Path, [string]$Method='GET', $Data=$null) {
    $requestParameters = @{ Uri=$base + $Path; Method=$Method; Headers=$headers; TimeoutSec=60 }
    if ($null -ne $Data) { $requestParameters.Body = [Text.Encoding]::UTF8.GetBytes(($Data | ConvertTo-Json -Depth 10)); $requestParameters.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @requestParameters
}
if ((Request '').private) { throw 'Expected the already-public repository; visibility will not be changed by this script.' }
if ((Request '/commits/main').sha -ne $sha) { throw 'Push the release commit to main before publishing.' }
$releases = @(Request '/releases')
$release = $releases | Where-Object tag_name -eq $tag | Select-Object -First 1
if (-not $release) {
    $release = Request '/releases' 'POST' @{ tag_name=$tag; target_commitish=$sha; name="OEPS Scanner $tag"; body=$body; draft=$true; prerelease=$false }
}
if ($release.target_commitish -ne $sha) { throw 'Release points to a different commit.' }
$existing = @(Request ("/releases/" + $release.id + '/assets'))
foreach ($name in $names) {
    $path = Join-Path $repo ('artifacts\' + $name)
    $file = Get-Item -LiteralPath $path
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = $existing | Where-Object name -eq $name | Select-Object -First 1
    if ($asset) {
        if ($asset.size -ne $file.Length -or $asset.digest -ne $digest) { throw "Existing asset differs: $name" }
        Write-Output "Already uploaded and verified: $name"
        continue
    }
    if (-not $release.draft) { throw 'Will not modify assets of a published release.' }
    $upload = [Uri](($release.upload_url -split '\{')[0] + '?name=' + [Uri]::EscapeDataString($name))
    if ($upload.Scheme -ne 'https' -or $upload.Host -ne 'uploads.github.com') { throw 'Unexpected upload host.' }
    $asset = Invoke-RestMethod -Uri $upload -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $path -TimeoutSec 300
    if ($asset.size -ne $file.Length -or $asset.digest -ne $digest) { throw "Uploaded asset checksum differs: $name" }
    Write-Output "Uploaded and verified: $name"
}
$assets = @(Request ("/releases/" + $release.id + '/assets'))
if (Compare-Object ($names | Sort-Object) ($assets.name | Sort-Object)) { throw 'Unexpected release asset list.' }
if ($release.draft) { $release = Request ("/releases/" + $release.id) 'PATCH' @{ draft=$false; make_latest='true'; body=$body } }
$latest = Invoke-RestMethod -Uri ($base + '/releases/latest') -Headers @{ 'User-Agent'='OEPS-Scanner-Release' } -TimeoutSec 60
if ($latest.tag_name -ne $tag -or $latest.draft) { throw 'Public latest release verification failed.' }
[ordered]@{ release_url=$latest.html_url; commit=$sha; version=$Version; assets=$assets.Count } | ConvertTo-Json
