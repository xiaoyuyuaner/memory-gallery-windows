param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'release'
$folderName = 'MemoryGallery-' + $Version + '-windows-portable'
$packageFolder = Join-Path $releaseRoot $folderName
$zipPath = Join-Path $releaseRoot ($folderName + '.zip')

if (Test-Path -LiteralPath $packageFolder) { throw "Release directory already exists: $packageFolder" }
if (Test-Path -LiteralPath $zipPath) { throw "Release archive already exists: $zipPath" }

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
& (Join-Path $projectRoot 'build.ps1') -OutputDirectory $packageFolder
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $packageFolder
Compress-Archive -LiteralPath $packageFolder -DestinationPath $zipPath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$hashLine = $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zipPath)
$hashFile = $zipPath + '.sha256'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($hashFile, $hashLine + [Environment]::NewLine, $utf8NoBom)

Get-Item -LiteralPath $zipPath, $hashFile | Select-Object FullName, Length

