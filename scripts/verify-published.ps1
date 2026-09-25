param([Parameter(Mandatory)][string]$ApplicationDirectory,[Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$smokeDirectory = Join-Path $repo '.local\published-smoke'
New-Item -ItemType Directory -Force -Path $smokeDirectory | Out-Null
$env:DOTNET_ROOT = $runtime
$env:DOTNET_ROOT_X64 = $runtime
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$serverData = Join-Path $smokeDirectory 'server'
$serverArguments = '--ui-smoke --data-dir "' + $serverData + '"'
$serverProcess = Start-Process -FilePath (Join-Path $appDirectory 'Scanner.Server.exe') -ArgumentList $serverArguments -WorkingDirectory $appDirectory -WindowStyle Hidden -PassThru
try {
    if (-not $serverProcess.WaitForExit(20000)) { throw 'Published Server did not finish its UI smoke check.' }
    if ($serverProcess.ExitCode -ne 0) { throw "Published Server exited $($serverProcess.ExitCode)." }
    $report = Get-Content -LiteralPath (Join-Path $serverData 'server-ui-smoke.txt') -Raw
    if (-not $report.StartsWith('PASS:')) { throw $report }
    Write-Output $report.Trim()
} finally { if (-not $serverProcess.HasExited) { $serverProcess.Kill() }; $serverProcess.Dispose() }
$readyName = 'Local\OEPS.Scanner.Ready.' + [Guid]::NewGuid().ToString('N')
$ready = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $readyName)
$clientData = Join-Path $smokeDirectory 'client'
$clientArguments = '--startup-ready "' + $readyName + '" --data-dir "' + $clientData + '"'
$clientProcess = Start-Process -FilePath (Join-Path $appDirectory 'Scanner.TestClient.exe') -ArgumentList $clientArguments -WorkingDirectory $appDirectory -WindowStyle Hidden -PassThru
try {
    if (-not $ready.WaitOne(15000)) { throw 'Published Test Client did not acknowledge WPF startup readiness.' }
    $clientProcess.Refresh()
    if ($clientProcess.HasExited) { throw 'Published Test Client exited before startup validation.' }
    if (-not $clientProcess.CloseMainWindow()) { throw 'Published Test Client did not expose a native window for graceful close.' }
    if (-not $clientProcess.WaitForExit(10000)) { throw 'Published Test Client did not close gracefully.' }
    if ($clientProcess.ExitCode -ne 0) { throw "Published Test Client exited $($clientProcess.ExitCode)." }
    $result = 'PASS: published Test Client acknowledged native WPF readiness and closed gracefully using bundled .NET runtime.'
    [IO.File]::WriteAllText((Join-Path $smokeDirectory 'test-client.txt'),$result)
    Write-Output $result
} finally { if (-not $clientProcess.HasExited) { $clientProcess.Kill() }; $clientProcess.Dispose(); $ready.Dispose() }
