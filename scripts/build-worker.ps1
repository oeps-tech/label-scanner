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
& $pythonPath (Join-Path $PSScriptRoot 'stage-worker.py') $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Clean worker runtime staging failed.' }
& (Join-Path $outputPath 'python\python.exe') -c "import recognition.worker; print('Bundled worker import verified')"
if ($LASTEXITCODE -ne 0) { throw 'Bundled worker import failed.' }
Write-Output "Offline runtime bundled at $outputPath. No OCR models are required."
