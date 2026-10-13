param(
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('HOI4LoaderTest' + [Guid]::NewGuid().ToString('N'))),
    [string]$LoaderPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'work\AdmissionLoader.exe')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixturePath = Join-Path $WorkDirectory 'hoi4.exe'
& $compiler /nologo /target:exe /platform:x64 ('/out:' + $fixturePath) (Join-Path $PSScriptRoot 'PlainFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Unsupported-process fixture compile failed' }
$dll = Join-Path $root 'bundled-module\HOI4Admission.dll'
$ini = Join-Path $WorkDirectory 'fixture.ini'
@('[admission]', 'Session=fixture', 'Player=fixture', ('Secret=' + ('a' * 64)), ('DllHash=' + (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()), 'Pipe=\\.\pipe\unused-test', ('Manifest=' + (Join-Path $root 'bundled-module\mod-manifest.json')), ('Log=' + (Join-Path $WorkDirectory 'fixture.log'))) | Set-Content -LiteralPath $ini -Encoding Unicode
$fixture = Start-Process -FilePath $fixturePath -WindowStyle Hidden -PassThru
try {
    $creation = $fixture.StartTime.ToUniversalTime().ToFileTimeUtc()
    & $LoaderPath $fixture.Id $creation $dll $ini
    if ($LASTEXITCODE -eq 0) { throw 'Unsupported process was accepted by the native loader' }
    $fixture.Refresh()
    if (@($fixture.Modules | Where-Object ModuleName -eq 'HOI4Admission.dll').Count -ne 0) { throw 'Unsupported process was modified' }
    Write-Output 'PASS enabled native loader refuses unsupported executable before DLL loading'
} finally { $fixture.WaitForExit(); $fixture.Dispose() }
# The loader's intentional refusal is a passing test, not this script's exit status.
$global:LASTEXITCODE = 0
