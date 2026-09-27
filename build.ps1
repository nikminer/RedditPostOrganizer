param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore RedditPostOrganizer.sln --locked-mode
    if ($LASTEXITCODE) { throw 'Restore failed' }
    if (-not $SkipTests) {
        dotnet test RedditPostOrganizer.sln -c Release --no-restore
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
    $stage = Join-Path $PSScriptRoot ('work/publish-' + [guid]::NewGuid().ToString('N'))
    dotnet publish src/RedditPostOrganizer.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:RestoreLockedMode=true -o $stage
    if ($LASTEXITCODE) { throw 'Publish failed' }
    $exeName = 'Reddit Post Organizer.exe'
    Rename-Item -LiteralPath (Join-Path $stage 'RedditPostOrganizer.exe') -NewName $exeName
    Copy-Item README.md,API_ACCESS.md,VALIDATION.md -Destination $stage
    & ./scripts/collect-notices.ps1
    Copy-Item THIRD_PARTY_LICENSES -Destination $stage -Recurse
    $target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist/RedditPostOrganizer'))
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
    if ([IO.Path]::GetDirectoryName($target) -ne $expected) { throw 'Unexpected output path' }
    $active = Get-Process | Where-Object { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path) -eq $target) }
    if ($active) { throw 'Close Reddit Post Organizer using its Exit command before replacing the build.' }
    New-Item -ItemType Directory -Force -Path $expected,(Join-Path $PSScriptRoot 'releases') | Out-Null
    if (Test-Path -LiteralPath $target) {
        $previous = Join-Path $PSScriptRoot ('work/previous-runtime-' + [guid]::NewGuid().ToString('N'))
        if (-not ([IO.Path]::GetFullPath($previous).StartsWith([IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'work')) + [IO.Path]::DirectorySeparatorChar))) { throw 'Unexpected archive path' }
        Move-Item -LiteralPath $target -Destination $previous
    }
    Move-Item -LiteralPath $stage -Destination $target
    Compress-Archive -Path "$target/*" -DestinationPath releases/Reddit-Post-Organizer-Portable.zip -Force
    Write-Host "Ready: $target/$exeName"
} finally { Pop-Location }
