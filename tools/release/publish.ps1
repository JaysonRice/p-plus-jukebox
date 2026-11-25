# Builds the release zip: a self-contained Windows x64 build (no .NET install needed) in dist\.
# Run from the repo root:  powershell -File tools\release\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
[xml]$proj = Get-Content (Join-Path $root 'src\App\PPlusTracklistEditor.App.csproj')
$version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$name = "P+ Jukebox $version"
$out = Join-Path $root "dist\$name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'src\App') -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
# Never ship a library (songs, builds) or debug symbols.
if (Test-Path (Join-Path $out 'Library')) { throw 'A Library folder ended up in the build; refusing to zip it.' }
Get-ChildItem $out -Filter *.pdb | Remove-Item
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') $out
$zip = Join-Path $root "dist\P+ Jukebox $version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip
Write-Host "Done: $zip"
