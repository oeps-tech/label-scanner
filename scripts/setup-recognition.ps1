[CmdletBinding()]
param([string]$PythonDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PythonDirectory) { $PythonDirectory = Join-Path $projectRoot '.tools\python' }
$pythonPath = Join-Path $PythonDirectory 'python.exe'
if (-not (Test-Path -LiteralPath $pythonPath)) {
    New-Item -ItemType Directory -Force -Path $PythonDirectory | Out-Null
    $archive = Join-Path $projectRoot '.tools\python-3.12.10.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $PythonDirectory -Force
}
@('python312.zip', '.', $projectRoot, 'import site') | Set-Content -LiteralPath (Join-Path $PythonDirectory 'python312._pth') -Encoding ASCII
& $pythonPath -m pip --version
if ($LASTEXITCODE -ne 0) {
    $pipScript = Join-Path $projectRoot '.tools\get-pip.py'
    Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/get-pip.py' -OutFile $pipScript
    & $pythonPath $pipScript 'pip==26.2.1' --no-warn-script-location
    if ($LASTEXITCODE -ne 0) { throw 'pip bootstrap failed' }
}
$requirements = Join-Path $projectRoot 'recognition\requirements.lock'
if (-not (Test-Path -LiteralPath $requirements)) { $requirements = Join-Path $projectRoot 'recognition\requirements.in' }
& $pythonPath -u -m pip install -r $requirements --no-warn-script-location --disable-pip-version-check
if ($LASTEXITCODE -ne 0) { throw 'Recognition dependency installation failed' }
& $pythonPath -c 'import cv2,zxingcpp; print(cv2.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'Recognition runtime validation failed' }
