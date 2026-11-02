# Optional code signing for the packaging scripts (dot-sourced by package.ps1 / build_installer.ps1).
# Signing needs a code-signing certificate (.pfx) and signtool.exe (Windows SDK):
#   $env:MARKSHOT_SIGN_PFX      = 'C:\path\to\certificate.pfx'
#   $env:MARKSHOT_SIGN_PASSWORD = '...'
# Without MARKSHOT_SIGN_PFX nothing is signed (Windows SmartScreen then shows "unknown publisher").

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

function Invoke-CodeSign([string[]]$Files) {
    if (-not $env:MARKSHOT_SIGN_PFX) { return }
    if (-not (Test-Path $env:MARKSHOT_SIGN_PFX)) { throw "MARKSHOT_SIGN_PFX not found: $env:MARKSHOT_SIGN_PFX" }
    $signtool = Find-SignTool
    if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK (winget install Microsoft.WindowsSDK.10.0.22621)' }
    foreach ($file in $Files) {
        & $signtool sign /f $env:MARKSHOT_SIGN_PFX /p $env:MARKSHOT_SIGN_PASSWORD /fd SHA256 `
            /tr http://timestamp.digicert.com /td SHA256 /d Markshot $file | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signing failed: $file" }
    }
}
