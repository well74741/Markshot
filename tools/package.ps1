# Markshot 打包：检查依赖 → 生成便携版 zip 和安装包，输出到 dist\
#   tools\package.cmd                 两种都打
#   tools\package.cmd -NoInstaller    只打便携版
#   tools\package.cmd -NoPortable     只打安装包
#
# 依赖：
#   必需  .NET 8 SDK          winget install Microsoft.DotNet.SDK.8
#                             或 https://dotnet.microsoft.com/download/dotnet/8.0
#   安装包 Inno Setup 6        winget install JRSoftware.InnoSetup
#                             或 https://jrsoftware.org/isdl.php
#   可选  Python 3 + Pillow   只有改了 tools\make_icon.py / make_toolbar_icons.py 才需要重新生成图标
#                             pip install pillow
param(
    [switch]$NoInstaller,
    [switch]$NoPortable
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Find-Dotnet {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    foreach ($candidate in @($userDotnet, 'dotnet')) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if (-not $command) { continue }
        $sdks = & $command.Source --list-sdks 2>$null
        if ($sdks -match '^8\.') { return $command.Source }
    }
    return $null
}

function Find-Iscc {
    @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

Write-Host ''
Write-Host '检查依赖' -ForegroundColor Cyan
$missing = @()
$dotnet = Find-Dotnet
if ($dotnet) {
    Write-Host "  [OK] .NET 8 SDK    $dotnet"
} else {
    Write-Host '  [缺] .NET 8 SDK    winget install Microsoft.DotNet.SDK.8   (https://dotnet.microsoft.com/download/dotnet/8.0)' -ForegroundColor Yellow
    $missing += '.NET 8 SDK'
}

if (-not $NoInstaller) {
    $iscc = Find-Iscc
    if ($iscc) {
        Write-Host "  [OK] Inno Setup 6  $iscc"
    } else {
        Write-Host '  [缺] Inno Setup 6  winget install JRSoftware.InnoSetup   (https://jrsoftware.org/isdl.php)，或加 -NoInstaller 只打便携版' -ForegroundColor Yellow
        $missing += 'Inno Setup 6'
    }
}

$python = Get-Command python -ErrorAction SilentlyContinue
$pillow = $false
if ($python) {
    & $python.Source -c 'import PIL' 2>$null
    $pillow = ($LASTEXITCODE -eq 0)
}
if ($pillow) {
    Write-Host '  [OK] Python+Pillow（可选，仅重新生成图标时用）'
} else {
    Write-Host '  [--] Python+Pillow（可选，仅修改图标脚本时需要：pip install pillow）' -ForegroundColor DarkGray
}

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host ("缺少：" + ($missing -join '、') + "。装好后重新运行。") -ForegroundColor Red
    exit 1
}

[xml]$project = Get-Content (Join-Path $root 'Markshot.csproj') -Encoding UTF8
$group = $project.Project.PropertyGroup | Where-Object { $_.InformationalVersion } | Select-Object -First 1
$version = $group.InformationalVersion
Write-Host ''
Write-Host "Markshot $version" -ForegroundColor Cyan

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if ($dotnet -like "$env:USERPROFILE*") { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not $NoPortable) {
    Write-Host '打包便携版…'
    $portable = Join-Path $root 'publish_portable'
    if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
    & $dotnet publish Markshot.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $portable | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
    Get-ChildItem $portable -Filter *.lib -Recurse | Remove-Item -Force
    $zip = Join-Path $dist "Markshot-$version-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "  便携版：$zip" -ForegroundColor Green
}

if (-not $NoInstaller) {
    Write-Host '打包安装包…'
    & (Join-Path $PSScriptRoot 'build_installer.ps1')
    Write-Host "  安装包：$(Join-Path $dist "Markshot-Setup-$version.exe")" -ForegroundColor Green
}

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
