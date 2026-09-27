param([string]$Destination = (Join-Path $PSScriptRoot '../THIRD_PARTY_LICENSES'))
$ErrorActionPreference = 'Stop'
$assets = Get-Content (Join-Path $PSScriptRoot '../src/Gallery.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$cacheRoot = ($assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Third-party dependency notices')
$lines.Add('')
$lines.Add('Includes runtime and build dependencies resolved by NuGet. Platform-specific assets not used on Windows are listed for completeness. Original bundled license and notice files are retained in subdirectories.')
foreach ($lib in $assets.libraries.PSObject.Properties) {
    if ($lib.Value.type -ne 'package') { continue }
    $packageDir = Join-Path $cacheRoot $lib.Value.path
    $specFile = Get-ChildItem -LiteralPath $packageDir -Filter *.nuspec | Select-Object -First 1
    [xml]$spec = Get-Content -LiteralPath $specFile.FullName -Raw
    $meta = $spec.package.metadata
    $license = if ($meta.license) { $meta.license.InnerText } else { [string]$meta.licenseUrl }
    $lines.Add('')
    $lines.Add('## ' + $lib.Name)
    $lines.Add('Authors: ' + $meta.authors)
    $lines.Add('Copyright: ' + $meta.copyright)
    $lines.Add('License: ' + $license)
    $lines.Add('Source: ' + $meta.repository.url)
    $dest = Join-Path $Destination ($lib.Name.Replace('/','-'))
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -LiteralPath $specFile.FullName -Destination $dest
    Get-ChildItem -LiteralPath $packageDir -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD.PARTY)' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dest }
}
$runtime = Join-Path $cacheRoot 'microsoft.netcore.app.runtime.win-x64/10.0.9'
if (Test-Path -LiteralPath $runtime) {
    $dest = Join-Path $Destination 'dotnet-runtime-10.0.9'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtime 'LICENSE.TXT'),(Join-Path $runtime 'THIRD-PARTY-NOTICES.TXT') -Destination $dest
}
$lines | Set-Content (Join-Path $Destination 'NOTICES.md') -Encoding utf8
