param(
    [string]$Zig = 'zig',
    [string]$WorkDirectory = (Join-Path $PSScriptRoot 'work'),
    [string]$CacheDirectory = $WorkDirectory,
    [string]$BundleDirectory = (Join-Path $PSScriptRoot 'bundled-module'),
    [string]$DistDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$updaterSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'updater\ModuleUpdater.cs') -Raw
$versionMatch = [regex]::Match($updaterSource, 'public const string InjectorVersion = "([0-9]+\.[0-9]+\.[0-9]+)";')
if (-not $versionMatch.Success) { throw 'Injector release version is missing' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne ('v' + $versionMatch.Groups[1].Value)) { throw 'Release tag does not match injector version' }
New-Item -ItemType Directory -Force -Path $WorkDirectory, $CacheDirectory, $DistDirectory | Out-Null
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $CacheDirectory 'zig-global'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $CacheDirectory 'zig-local'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The .NET Framework 4.8 C# compiler is required.' }
$moduleFiles = @('HOI4Admission.dll', 'AdmissionService.exe', 'mod-manifest.json', 'module-release.json')
foreach ($name in $moduleFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $BundleDirectory $name) -PathType Leaf)) { throw ('Missing bundled module file: ' + $name) }
}
$descriptor = Get-Content -LiteralPath (Join-Path $BundleDirectory 'module-release.json') -Raw | ConvertFrom-Json
if ($descriptor.schema -ne 1 -or $descriptor.protocol -ne 2 -or $descriptor.minimumInjectorVersion -ne '0.4.0' -or $descriptor.gameVersion -ne '1.19.3.0' -or $descriptor.gameSha256 -ne '7dc947be34970da1e1c787bcddbe7f62b610ff258aebf8f06aa8b348f3a031d5') { throw 'Unsupported bundled module descriptor.' }
$requiredModuleFiles = @('HOI4Admission.dll', 'AdmissionService.exe', 'mod-manifest.json')
if (@($descriptor.files).Count -ne 3) { throw 'Bundled module descriptor must declare exactly three files.' }
$seen = @{}
foreach ($file in $descriptor.files) {
    if ($requiredModuleFiles -cnotcontains $file.path -or $seen.ContainsKey($file.path) -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid bundled module digest record.' }
    $seen[$file.path] = $true
    if ((Get-FileHash -LiteralPath (Join-Path $BundleDirectory $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw ('Bundled module digest mismatch: ' + $file.path) }
}
$references = @('/reference:System.Core.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Xml.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll')
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'manager') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'updater') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'shared\Shared.cs'
& $compiler /nologo /langversion:5 /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ ('/out:' + (Join-Path $WorkDirectory 'HOI4Injector.exe')) ('/win32manifest:' + (Join-Path $PSScriptRoot 'manager\app.manifest')) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Injector GUI build failed.' }
& $Zig cc -target x86_64-windows-gnu -municode -std=c11 -O2 -Wall -Wextra -Werror -o (Join-Path $WorkDirectory 'AdmissionLoader.exe') (Join-Path $PSScriptRoot 'native\loader.c') -lbcrypt
if ($LASTEXITCODE -ne 0) { throw 'Native loader build failed.' }
$installerSources = @((Join-Path $PSScriptRoot 'installer\InstallCore.cs'), (Join-Path $PSScriptRoot 'installer\AssemblyInfo.cs'))
& $compiler /nologo /langversion:5 /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ ('/out:' + (Join-Path $WorkDirectory 'Uninstall.exe')) ('/win32manifest:' + (Join-Path $PSScriptRoot 'installer\app.manifest')) @references @installerSources (Join-Path $PSScriptRoot 'installer\Uninstall.cs')
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller build failed.' }
$stage = Join-Path $WorkDirectory ('portable-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'bundled-module') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $WorkDirectory 'HOI4Injector.exe'), (Join-Path $WorkDirectory 'AdmissionLoader.exe'), (Join-Path $WorkDirectory 'Uninstall.exe'), (Join-Path $PSScriptRoot 'HOI4Injector.exe.config'), (Join-Path $PSScriptRoot 'release-source.json') -Destination $stage
foreach ($name in $moduleFiles) { Copy-Item -LiteralPath (Join-Path $BundleDirectory $name) -Destination (Join-Path $stage 'bundled-module') }
@'
HOI4 Injector 0.4.0 - Windows x64

Portable: extract all files into a folder, then run HOI4Injector.exe.
Installer: run HOI4Injector-Setup.exe and select Install.
Start the injector, then launch HOI4 normally through Steam.
The injector detects hoi4.exe and loads the admission mod automatically.

The mod updates from Razzyk/hoi4-admission-mod GitHub releases.
Install updates after closing every HOI4 copy that uses the old DLL.
Settings and downloads are saved separately in %LOCALAPPDATA%\HOI4Injector.
This mod currently supports HOI4 1.19.3.0 and local test sessions.

Source and releases: https://github.com/Razzyk/hoi4-injector
Admission mod: https://github.com/Razzyk/hoi4-admission-mod
'@ | Set-Content -LiteralPath (Join-Path $stage 'README-Run.txt') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portable = Join-Path $DistDirectory 'HOI4-Injector-win-x64.zip'
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $portable, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$setup = Join-Path $DistDirectory 'HOI4Injector-Setup.exe'
& $compiler /nologo /langversion:5 /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ ('/out:' + $setup) ('/win32manifest:' + (Join-Path $PSScriptRoot 'installer\app.manifest')) ('/resource:' + $portable + ',HOI4Installer.Payload.zip') @references @installerSources (Join-Path $PSScriptRoot 'installer\Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$checksums = foreach ($asset in @($portable, $setup)) { ((Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($asset)) }
$checksums | Set-Content -LiteralPath (Join-Path $DistDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output ('Built HOI4 Injector, native loader, portable ZIP and current-user setup in ' + $DistDirectory)
