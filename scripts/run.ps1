param(
    [ValidateSet('Server','TestClient','Both')][string]$App = 'Both',
    [switch]$NoBuild,
    [switch]$PrepareOnly,
    [string]$DataDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$python = Join-Path $repo '.tools\python\python.exe'
if (-not (Test-Path -LiteralPath $dotnet) -or ($App -ne 'TestClient' -and -not (Test-Path -LiteralPath $python))) {
    throw 'Local runtimes are missing. Run powershell -ExecutionPolicy Bypass -File scripts/setup.ps1 first.'
}
if (-not $DataDirectory) { $DataDirectory = Join-Path $repo '.local\run' }
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$env:DOTNET_ROOT = Join-Path $repo '.tools\dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
foreach ($name in $(if ($App -eq 'Both') { @('Server','TestClient') } else { @($App) })) {
    $path = Join-Path $repo "src\Scanner.$name\bin\Debug\net10.0-windows\Scanner.$name.exe"
    if (-not (Test-Path -LiteralPath $path)) { throw "Local $name build is missing. Run again without -NoBuild." }
    $data = Join-Path $DataDirectory $(if ($name -eq 'Server') { 'server' } else { 'client' })
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    if ($PrepareOnly) { continue }
    # These are the interactive operator windows requested by this launch command.
    Start-Process -FilePath $path -ArgumentList ('--data-dir "' + $data + '"') -WorkingDirectory $repo -WindowStyle Normal | Out-Null
    Write-Output "Started $name. Local data: $data"
}
if ($PrepareOnly) { Write-Output "Local run ready. Data directory: $DataDirectory" }
