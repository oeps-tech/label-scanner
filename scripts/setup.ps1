param([switch]$SkipPython)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkVersion = (Get-Content -Raw (Join-Path $repo 'global.json') | ConvertFrom-Json).sdk.version
$dotnetRoot = Join-Path $repo '.tools\dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    New-Item -ItemType Directory -Force -Path $dotnetRoot | Out-Null
    $metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
    $sdk = @($metadata.releases | ForEach-Object { $_.sdks; $_.sdk } | Where-Object version -eq $sdkVersion)[0]
    if (-not $sdk) { throw "Official release metadata does not include .NET SDK $sdkVersion." }
    $file = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') })[0]
    if (-not $file) { throw 'No compatible Windows x64 SDK archive was published.' }
    $download = Join-Path $repo '.tools\dotnet-sdk.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $file.url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA-512 mismatch.' }
    Expand-Archive -LiteralPath $download -DestinationPath $dotnetRoot -Force
}
$env:DOTNET_CLI_HOME = Join-Path $repo '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $repo '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnet restore (Join-Path $repo 'OEPS.Scanner.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw '.NET dependency restore failed.' }
if (-not $SkipPython) {
    $pythonSetup = Join-Path $PSScriptRoot 'setup-recognition.ps1'
    if (Test-Path -LiteralPath $pythonSetup) { & $pythonSetup }
    else { throw 'Missing scripts/setup-recognition.ps1' }
}
Write-Output "Pinned .NET SDK $sdkVersion is ready at $dotnet."
