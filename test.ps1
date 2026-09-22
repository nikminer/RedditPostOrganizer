$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) { throw 'Run setup-dev.ps1 first.' }
    $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $PSScriptRoot '.browsers'
    & $python -m pytest @args
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
