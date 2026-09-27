$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try { dotnet run --project src/RedditPostOrganizer.Desktop -- @args; if ($LASTEXITCODE) { throw 'Run failed' } }
finally { Pop-Location }
