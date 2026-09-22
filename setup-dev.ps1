$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not (Test-Path -LiteralPath '.venv\Scripts\python.exe')) {
        & py -3.12 -m venv .venv
        if ($LASTEXITCODE -ne 0) { throw 'Python 3.12 is required to create the development environment.' }
    }
    $python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
    & $python -m pip install -r requirements-lock.txt
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    & $python -m pip install -e .
    if ($LASTEXITCODE -ne 0) { throw 'Project installation failed.' }
    $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $PSScriptRoot '.browsers'
    & $python -m playwright install chromium --no-shell
    if ($LASTEXITCODE -ne 0) { throw 'Chromium installation failed.' }
    Write-Host 'Ready. Use run-dev.ps1, test.ps1 or build.ps1.'
} finally { Pop-Location }
