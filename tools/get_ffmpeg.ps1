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
# GitHub's release downloads fail now and then (502 / 504): a few tries, a little apart.
function Fetch($url, $file) {
    for ($try = 1; ; $try++) {
        try { Invoke-WebRequest $url -OutFile $file -UseBasicParsing; return }
        catch {
            if ($try -ge 6) { throw }
            Write-Host "  下载失败（$($_.Exception.Message)），$($try * 10) 秒后重试…"
            Start-Sleep -Seconds ($try * 10)
        }
    }
}
Fetch "$base/ffmpeg-win32-x64.gz" $gz
$in = [IO.File]::OpenRead($gz)
try {
    $unzip = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
    $out = [IO.File]::Create($exe)
    try { $unzip.CopyTo($out) } finally { $out.Dispose(); $unzip.Dispose() }
} finally { $in.Dispose() }
Remove-Item $gz
Fetch "$base/win32-x64.LICENSE" (Join-Path $target 'ffmpeg.exe.LICENSE')
Fetch "$base/win32-x64.README" (Join-Path $target 'ffmpeg.exe.README')
Write-Host "  [OK] ffmpeg         $exe"
