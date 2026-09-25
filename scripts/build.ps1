param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_HOME = Join-Path $repo '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $repo '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& (Join-Path $repo '.tools\dotnet\dotnet.exe') build (Join-Path $repo 'OEPS.Scanner.sln') -c $Configuration -p:RestoreLockedMode=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
