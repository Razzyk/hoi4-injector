param([string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$injectorRoot = Split-Path $PSScriptRoot -Parent
if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('H4Coordinator-' + [guid]::NewGuid().ToString('N')) }
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test = Join-Path $WorkDirectory 'CoordinatorTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $injectorRoot 'manager') -Filter '*.cs' | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $injectorRoot 'updater') -Filter '*.cs' | ForEach-Object FullName)
$sources += @((Join-Path $injectorRoot 'shared\Shared.cs'), (Join-Path $PSScriptRoot 'CoordinatorTests.cs'))
& $compiler /nologo /langversion:5 /target:exe /main:CoordinatorTests /platform:x64 /optimize+ /warn:4 /warnaserror+ ('/out:' + $test) /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Coordinator test compilation failed' }
& $test (Join-Path $WorkDirectory ('fixtures-' + [guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw 'Coordinator checks failed' }
