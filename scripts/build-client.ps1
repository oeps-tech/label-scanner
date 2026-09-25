param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_HOME = Join-Path $repo '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $repo '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
& $dotnet build (Join-Path $repo 'src\Scanner.Client\Scanner.Client.csproj') -c $Configuration -p:RestoreLockedMode=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Client DLL build failed.' }
$source = Join-Path $repo "src\Scanner.Client\bin\$Configuration\net10.0"
$output = Join-Path $repo '.local\client-sdk'
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($name in @('Scanner.Client.dll','Scanner.Client.xml','Scanner.Contracts.dll','Scanner.Contracts.xml')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $output -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'docs\client-library.md') -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'examples\Scanner.LabelConsumer\Program.cs') -Destination (Join-Path $output 'Example.cs') -Force
Write-Output "Local client DLLs ready: $output"
