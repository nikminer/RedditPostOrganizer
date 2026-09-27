$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try { dotnet restore RedditGallery.sln --locked-mode; if ($LASTEXITCODE) { throw 'Restore failed' } }
finally { Pop-Location }
