$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try { dotnet run --project src/Gallery.Desktop -- @args; if ($LASTEXITCODE) { throw 'Run failed' } }
finally { Pop-Location }
