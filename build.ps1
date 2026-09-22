param([string]$Python = "$PSScriptRoot\.venv\Scripts\python.exe", [string]$BrowserPath = "$PSScriptRoot\.browsers")
$ErrorActionPreference = 'Stop'
$OriginalPath = $env:PATH
$Python = (Get-Command $Python -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try {
    & $Python -m pip install -r requirements-lock.txt
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency installation failed' }
    & $Python -m pip install -e '.[dev]'
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
    $env:PLAYWRIGHT_BROWSERS_PATH = $BrowserPath
    & $Python -m playwright install chromium --no-shell
    if ($LASTEXITCODE -ne 0) { throw 'Chromium installation failed' }
    # Keep unrelated tools' ICU DLLs out of the Qt bundle. Qt uses Windows ICU.
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot;$(Split-Path $Python)"
    & $Python -m PyInstaller --clean --noconfirm --windowed --name RedditGallery --collect-data tzdata --collect-data playwright launch.py
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Copy-Item -LiteralPath $BrowserPath -Destination "$PSScriptRoot\dist\RedditGallery\browsers" -Recurse -Force
    Copy-Item -LiteralPath "$PSScriptRoot\README.md" -Destination "$PSScriptRoot\dist\RedditGallery\README.md" -Force
    & $Python collect_licenses.py "$PSScriptRoot\dist\RedditGallery"
    Write-Host 'Ready: dist\RedditGallery\RedditGallery.exe'
} finally { $env:PATH = $OriginalPath; Pop-Location }
