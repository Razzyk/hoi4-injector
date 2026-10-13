param([string]$WorkDirectory = (Join-Path $PSScriptRoot '..\work\installer-tests'), [string]$SetupPath = (Join-Path $PSScriptRoot '..\dist\HOI4Injector-Setup.exe'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test = Join-Path $WorkDirectory 'InstallerTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warn:4 /warnaserror+ ('/out:' + $test) /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot '..\installer\InstallCore.cs') (Join-Path $PSScriptRoot 'InstallerTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer test compilation failed.' }
$fixture = Join-Path $WorkDirectory ('core-' + [guid]::NewGuid().ToString('N'))
& $test $fixture
if ($LASTEXITCODE -ne 0) { throw 'Installer safety tests failed.' }
if (Test-Path -LiteralPath $SetupPath -PathType Leaf) {
    $extraction = [System.IO.Path]::GetFullPath((Join-Path $WorkDirectory ('setup-' + [guid]::NewGuid().ToString('N'))))
    $process = Start-Process -FilePath $SetupPath -ArgumentList @('--extract-only', ('"' + $extraction + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Embedded setup extraction failed: ' + $process.ExitCode) }
    foreach ($name in @('HOI4Injector.exe', 'AdmissionLoader.exe', 'Uninstall.exe', 'bundled-module\HOI4Admission.dll', 'bundled-module\AdmissionService.exe', 'bundled-module\module-release.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $extraction $name) -PathType Leaf)) { throw ('Setup failed to extract ' + $name) }
    }
    Write-Output 'PASS: embedded setup --extract-only integration test'
}
