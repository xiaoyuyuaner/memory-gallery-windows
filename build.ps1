param([string]$OutputDirectory, [switch]$IncludeTests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$csc = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw 'Windows .NET Framework 4.8 is required.' }
$dist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist' }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$iconFile = Join-Path $projectRoot 'memory-gallery.ico'
if (!(Test-Path -LiteralPath $iconFile)) { throw 'Missing memory-gallery.ico.' }
$references = @(
    'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll',
    'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll','WPF\UIAutomationProvider.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$sources = @(
    'MemoryGallery.cs','Memo.cs','Hover.cs','Startup.cs','Remark.cs',
    'ShelfMotion.cs','FallingPaper.cs','HoverEffects.cs'
) | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',
    ('/out:' + (Join-Path $dist 'MemoryGallery.exe')),
    ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),
    ('/win32icon:' + $iconFile)
) + $references + $sources
if ($IncludeTests) {
    $arguments += '/define:TESTS'
    $arguments += @('HoverTests.cs','StartupTests.cs','RemarkTests.cs','InteractionTests.cs') |
        ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
}
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'MemoryGallery.exe.config') -Destination (Join-Path $dist 'MemoryGallery.exe.config') -Force
foreach ($name in @('使用说明.txt','LICENSE.txt','THIRD_PARTY_NOTICES.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $name)) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $dist -Force }
}
Get-Item -LiteralPath (Join-Path $dist 'MemoryGallery.exe') | Select-Object FullName,Length
