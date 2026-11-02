# Builds dist\Markshot-Setup-<version>.exe:
#   1. publishes self-contained Markshot (folder layout) into publish_installer\
#   2. compiles installer\Markshot.iss with Inno Setup 6 (ISCC)
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\build_installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$project = Get-Content (Join-Path $root 'Markshot.csproj') -Encoding UTF8
$group = $project.Project.PropertyGroup | Where-Object { $_.InformationalVersion } | Select-Object -First 1
$version = $group.InformationalVersion
$fileVersion = $group.FileVersion
Write-Host "Markshot $version ($fileVersion)"

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $dotnet) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet' } else { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$out = Join-Path $root 'publish_installer'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# A plain folder, not single-file: Inno's solid LZMA2 packs the loose files far smaller
# than the already-compressed single-file bundle (about 80 MB instead of 105 MB).
& $dotnet publish Markshot.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found: https://jrsoftware.org/isdl.php' }

& $iscc /Q "/DAppVersion=$version" "/DFileVersion=$fileVersion" (Join-Path $root 'installer\Markshot.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Write-Host "Done: $(Join-Path $root "dist\Markshot-Setup-$version.exe")"
