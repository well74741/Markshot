# Runs Markshot's test suites, each in its own process, and fails when any check fails.
#   powershell -ExecutionPolicy Bypass -File tests\run.ps1            all suites
#   powershell -ExecutionPolicy Bypass -File tests\run.ps1 batch1 menu  only these
# A suite fails when it prints a "FAIL …" line, crashes ("Unhandled exception") or exits non-zero.
# Screenshots the suites write go to tests\out\.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Suites)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$all = @('batch1', 'batch2', 'batch3', 'batch4', 'batch5', 'batch6', 'batch7', 'batch8', 'batch9',
         'editor', 'cornerprobe', 'picker', 'menu', 'theme', 'support', 'history', 'stitch', 'imageops', 'imageeditor', 'annotate', 'capture', 'hover', 'files', 'reccore', 'recflow', 'round48', 'round53', 'round55', 'round56', 'round57', 'round58', 'round59', 'round60', 'round61', 'round62', 'round63', 'round64', 'round65')
if (-not $Suites -or $Suites.Count -eq 0) { $Suites = $all }

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $dotnet) { $env:DOTNET_ROOT = Split-Path -Parent $dotnet } else { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# The recorder suites need ffmpeg next to the app (tools\deps\ffmpeg, see tools\get_ffmpeg.ps1).
if ($Suites -match '^rec' -and -not (Test-Path (Join-Path $root 'tools\deps\ffmpeg\ffmpeg.exe'))) {
    & (Join-Path $root 'tools\get_ffmpeg.ps1')
}

$project = Join-Path $PSScriptRoot 'Markshot.Tests\Markshot.Tests.csproj'
& $dotnet build $project -c Debug | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Get-ChildItem (Join-Path $PSScriptRoot 'Markshot.Tests\bin') -Recurse -Filter 'Markshot.Tests.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'Markshot.Tests.exe not found' }

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$failed = @()
foreach ($suite in $Suites) {
    $log = Join-Path $out "$suite.log"
    $p = Start-Process -FilePath $exe.FullName -ArgumentList $suite -WorkingDirectory $out -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle # without this, ExitCode stays empty for redirected processes
    if (-not $p.WaitForExit(300000)) {
        $p.Kill()
        $failed += "$suite (timeout)"
        $tail = (Get-Content $log -Tail 3 -ErrorAction SilentlyContinue) -join ' | '
        Write-Host ("  [FAIL] {0,-12} timeout after 300 s; last lines: {1}" -f $suite, $tail) -ForegroundColor Red
        if ($env:GITHUB_ACTIONS) { Write-Host "::error title=$suite::timeout after 300 s; last lines: $tail" }
        continue
    }
    $text = (Get-Content $log -Raw -ErrorAction SilentlyContinue) + (Get-Content "$log.err" -Raw -ErrorAction SilentlyContinue)
    $passes = ([regex]::Matches($text, '(?m)^PASS ')).Count
    $fails = [regex]::Matches($text, '(?m)^FAIL .*$') | ForEach-Object { $_.Value }
    $crashed = $text -match 'Unhandled exception'
    if ($fails -or $crashed -or $p.ExitCode -ne 0) {
        $failed += $suite
        Write-Host ("  [FAIL] {0,-12} {1} pass" -f $suite, $passes) -ForegroundColor Red
        $fails | ForEach-Object { Write-Host "         $_" -ForegroundColor Red }
        if ($env:GITHUB_ACTIONS) {
            # Annotations are readable on the run page without downloading the logs.
            $first = ($fails + ($text -split "`n" | Select-String 'Unhandled|   at ' | Select-Object -First 4 | ForEach-Object { $_.Line })) -join ' | '
            if (-not $first) { $first = "exit code $($p.ExitCode); last lines: " + ((Get-Content $log -Tail 3 -ErrorAction SilentlyContinue) -join ' | ') }
            Write-Host "::error title=$suite::$first"
        }
        if ($crashed) { Write-Host ($text -split "`n" | Select-String 'Unhandled|   at Markshot' | Select-Object -First 6 | Out-String) -ForegroundColor Red }
    } else {
        Write-Host ("  [OK]   {0,-12} {1} pass" -f $suite, $passes) -ForegroundColor Green
    }
}

if ($failed.Count -gt 0) {
    Write-Host ("失败：" + ($failed -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host '全部通过' -ForegroundColor Green
