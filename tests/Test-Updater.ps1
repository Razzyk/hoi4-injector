param([string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$injectorRoot = Split-Path $PSScriptRoot -Parent
if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('HOI4InjectorUpdaterTests-' + [guid]::NewGuid().ToString('N')) }
$WorkDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $WorkDirectory 'UpdaterTests.exe'
$updaterFiles = @(Get-ChildItem -LiteralPath (Join-Path $injectorRoot 'updater') -Filter '*.cs' | ForEach-Object FullName)
$sources = $updaterFiles + @((Join-Path $PSScriptRoot 'UpdaterTests.cs'), (Join-Path $injectorRoot 'shared\Shared.cs'))
& $compiler /nologo /langversion:5 /warnaserror+ /target:exe "/out:$testExe" /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Updater test compilation failed.' }
& $testExe (Join-Path $WorkDirectory ('fixtures-' + [guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed.' }
