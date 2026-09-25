param([ValidateSet('Debug','Release')][string]$Configuration = 'Release',[switch]$SkipRecognition)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -Filter '*.csproj' -Recurse) {
    if ($project.Name -eq 'Scanner.Tests.csproj' -and -not $SkipRecognition) {
        & (Join-Path $repo '.tools\dotnet\dotnet.exe') run --project $project.FullName -c $Configuration --no-build -- --worker
    } else {
        & (Join-Path $repo '.tools\dotnet\dotnet.exe') run --project $project.FullName -c $Configuration --no-build
    }
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.Name)" }
}
if (-not $SkipRecognition) {
    Push-Location $repo
    try { & (Join-Path $repo '.tools\python\python.exe') -u -m unittest discover -s recognition/tests -v; if ($LASTEXITCODE -ne 0) { throw 'Recognition tests failed.' } }
    finally { Pop-Location }
}
