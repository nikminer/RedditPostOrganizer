$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try { dotnet test RedditGallery.sln -c Release; if ($LASTEXITCODE) { throw 'Tests failed' } }
finally { Pop-Location }
