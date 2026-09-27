$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try { dotnet restore RedditPostOrganizer.sln --locked-mode; if ($LASTEXITCODE) { throw 'Restore failed' } }
finally { Pop-Location }
