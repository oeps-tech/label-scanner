[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Output, [string]$PythonDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PythonDirectory) { $PythonDirectory = Join-Path $projectRoot '.tools\python' }
$pythonPath = Join-Path $PythonDirectory 'python.exe'
if (-not (Test-Path -LiteralPath $pythonPath)) { throw 'Run scripts/setup-recognition.ps1 before packaging.' }
& $pythonPath -c "import cv2,zxingcpp; print('Recognition runtime dependencies verified')"
if ($LASTEXITCODE -ne 0) { throw 'Runtime dependencies are incomplete.' }
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$runtimeDestination = Join-Path $outputPath 'python'
& robocopy $PythonDirectory $runtimeDestination /E /R:1 /W:1 /XD __pycache__ /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { throw 'Runtime copy failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'recognition') -Destination (Join-Path $outputPath 'recognition') -Recurse -Force
# Embedded Python _pth paths are relative to python.exe. Use an explicit trailing separator
# for the parent: Windows normalizes bare '..' unexpectedly in isolated startup.
@('python312.zip', '.', '..\', 'import site') | Set-Content -LiteralPath (Join-Path $outputPath 'python\python312._pth') -Encoding ASCII
& (Join-Path $outputPath 'python\python.exe') -c "import recognition.worker; print('Bundled worker import verified')"
if ($LASTEXITCODE -ne 0) { throw 'Bundled worker import failed.' }
Write-Output "Offline runtime bundled at $outputPath. No OCR models are required."
