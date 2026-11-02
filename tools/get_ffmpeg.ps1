# Puts ffmpeg.exe (used by 录屏 for encoding / export) into tools\deps\ffmpeg\.
# The build copies it next to Markshot.exe (ffmpeg\ffmpeg.exe).
#   1. already there        -> nothing to do
#   2. $env:MARKSHOT_FFMPEG  -> copy that ffmpeg.exe
#   3. otherwise            -> download the gyan.dev 6.1.1 essentials build (GPL v3) that the
#                              ffmpeg-static project republishes on GitHub
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'tools\deps\ffmpeg'
$exe = Join-Path $target 'ffmpeg.exe'
if (Test-Path $exe) {
    Write-Host "  [OK] ffmpeg         $exe"
    return
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
if ($env:MARKSHOT_FFMPEG -and (Test-Path $env:MARKSHOT_FFMPEG)) {
    Copy-Item $env:MARKSHOT_FFMPEG $exe
    foreach ($extra in @('LICENSE', 'README')) {
        $side = "$($env:MARKSHOT_FFMPEG).$extra"
        if (Test-Path $side) { Copy-Item $side (Join-Path $target "ffmpeg.exe.$extra") }
    }
    Write-Host "  [OK] ffmpeg         copied from $env:MARKSHOT_FFMPEG"
    return
}

$base = 'https://github.com/eugeneware/ffmpeg-static/releases/download/b6.1.1'
Write-Host "  下载 ffmpeg（$base）…"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$gz = Join-Path $target 'ffmpeg.gz'
Invoke-WebRequest "$base/ffmpeg-win32-x64.gz" -OutFile $gz -UseBasicParsing
$in = [IO.File]::OpenRead($gz)
try {
    $unzip = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
    $out = [IO.File]::Create($exe)
    try { $unzip.CopyTo($out) } finally { $out.Dispose(); $unzip.Dispose() }
} finally { $in.Dispose() }
Remove-Item $gz
Invoke-WebRequest "$base/win32-x64.LICENSE" -OutFile (Join-Path $target 'ffmpeg.exe.LICENSE') -UseBasicParsing
Invoke-WebRequest "$base/win32-x64.README" -OutFile (Join-Path $target 'ffmpeg.exe.README') -UseBasicParsing
Write-Host "  [OK] ffmpeg         $exe"
