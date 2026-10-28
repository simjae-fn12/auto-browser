# Builds dist\oinzi-setup-<version>.exe: a self-contained publish, then the Inno Setup installer around it.
$ErrorActionPreference = 'Stop'
$native = Split-Path $PSScriptRoot
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required: winget install JRSoftware.InnoSetup' }
$out = Join-Path $native 'publish-installer'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $native 'native.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $out -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
& $iscc /Q (Join-Path $PSScriptRoot 'oinzi.iss')
if ($LASTEXITCODE) { throw 'Inno Setup failed' }
Get-ChildItem (Join-Path $native 'dist') -Filter 'oinzi-setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 FullName, Length
