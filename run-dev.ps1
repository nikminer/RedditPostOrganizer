$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) { throw 'Run setup-dev.ps1 first.' }
    $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $PSScriptRoot '.browsers'
    & $python -m reddit_gallery @args
    if ($LASTEXITCODE -ne 0) { throw 'Application exited with an error.' }
} finally { Pop-Location }
