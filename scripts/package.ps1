param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version = '0.1.0',
    [switch]$SkipMsi,
    [switch]$SkipTests,
    [string]$WorkerDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $repo '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $repo '.local\nuget'
$env:DOTNET_ROOT = Join-Path $repo '.tools\dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$output = Join-Path $repo 'artifacts'
$stage = Join-Path $output ('p-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$appStage = Join-Path $stage 'app-package\app'
$fullStage = Join-Path $stage 'full-installer'
$name = "OEPS.Scanner-$Version-win-x64.zip"
$zipPath = Join-Path $output $name
$msiPath = Join-Path $output "OEPS.Scanner-$Version-setup-win-x64.msi"
$sdkZip = Join-Path $output "OEPS.Scanner.Client-$Version.zip"
foreach ($target in @($zipPath, ($zipPath + '.sha256'), $msiPath, ($msiPath + '.sha256'), $sdkZip, ($sdkZip + '.sha256'))) {
    if (Test-Path -LiteralPath $target) { throw "Release artifact already exists: $target. Archive it or select a new version." }
}
New-Item -ItemType Directory -Force -Path $appStage,$fullStage | Out-Null
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') -Configuration Release }
foreach ($project in @('Server','TestClient')) {
    & $dotnet publish (Join-Path $repo "src\Scanner.$project") -c Release -r win-x64 --self-contained false -p:RestoreLockedMode=true -p:Version=$Version -p:CopyOutputSymbolsToPublishDirectory=false -o $appStage --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project." }
}
& $dotnet publish (Join-Path $repo 'src\Scanner.Launcher') -c Release -r win-x64 --self-contained false -p:RestoreLockedMode=true -p:Version=$Version -p:CopyOutputSymbolsToPublishDirectory=false -p:AppHostRelativeDotNet=../runtime -p:AppHostDotNetSearch=AppRelative -o (Join-Path $fullStage 'launcher') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
if ($WorkerDirectory) {
    & robocopy ([IO.Path]::GetFullPath($WorkerDirectory)) (Join-Path $appStage 'worker') /E /R:1 /W:1 /XD __pycache__ /NFL /NDL /NJH /NJS
    if ($LASTEXITCODE -ge 8) { throw 'Worker runtime copy failed.' }
} else {
    & (Join-Path $PSScriptRoot 'build-worker.ps1') -Output (Join-Path $appStage 'worker')
}
foreach ($folder in @('config','docs')) {
    if (Test-Path -LiteralPath (Join-Path $repo $folder)) {
        & robocopy (Join-Path $repo $folder) (Join-Path $appStage $folder) /E /R:1 /W:1 /NFL /NDL /NJH /NJS
        if ($LASTEXITCODE -ge 8) { throw "Copy failed: $folder" }
    }
}
foreach ($notice in @('README.md','THIRD-PARTY-NOTICES.md')) { if (Test-Path -LiteralPath (Join-Path $repo $notice)) { Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination $appStage } }
$manifest = @{ version=$Version; runtimeMajor=10; architecture='x64'; executable='Scanner.Server.exe'; applications=@('Scanner.Server.exe','Scanner.TestClient.exe'); worker='worker' } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $appStage 'update-manifest.json'),$manifest,[Text.UTF8Encoding]::new($false))
& (Join-Path $repo '.tools\python\python.exe') (Join-Path $PSScriptRoot 'package-files.py') (Split-Path -Parent $appStage) $zipPath
if ($LASTEXITCODE -ne 0) { throw 'Release ZIP construction failed.' }
function Write-Checksum([string]$Path) {
    $value = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($Path)
    [IO.File]::WriteAllText(($Path + '.sha256'),($value + [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
}
Write-Checksum $zipPath
& (Join-Path $PSScriptRoot 'build-client.ps1') -Configuration Release -Version $Version
Compress-Archive -Path (Join-Path $repo '.local\client-sdk\*') -DestinationPath $sdkZip
Write-Checksum $sdkZip
Copy-Item -LiteralPath $zipPath,($zipPath + '.sha256') -Destination $fullStage
if (-not $SkipMsi) { & (Join-Path $PSScriptRoot 'Build-Msi.ps1') -Stage $fullStage -Version $Version -Output $msiPath -Dotnet $dotnet; Write-Checksum $msiPath }
foreach ($package in @('Contracts','Client')) {
    & $dotnet pack (Join-Path $repo "src\Scanner.$package") -c Release -p:Version=$Version -p:PackageVersion=$Version -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "$package NuGet packaging failed." }
}
Write-Output "Release ZIP: $zipPath"
if (-not $SkipMsi) { Write-Output "Installer: $msiPath" }
Write-Output "Staging retained for inspection: $stage"
