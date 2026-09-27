$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try { dotnet test RedditPostOrganizer.sln -c Release; if ($LASTEXITCODE) { throw 'Tests failed' } }
finally { Pop-Location }
