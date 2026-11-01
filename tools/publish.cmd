@echo off
rem Build a self-contained Markshot.exe into .\publish (no .NET install needed to run it).
rem Usage: double-click, or run "tools\publish.cmd" from the repository root.
setlocal
cd /d "%~dp0.."

set "DOTNET=dotnet"
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
    set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

tasklist /FI "IMAGENAME eq Markshot.exe" 2>nul | find /I "Markshot.exe" >nul
if not errorlevel 1 (
    echo Markshot is running - exit it from the tray icon first, then run this again.
    pause
    exit /b 1
)

"%DOTNET%" publish Markshot.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -o publish
if errorlevel 1 (
    echo Publish failed.
    pause
    exit /b 1
)

echo.
echo Done: %CD%\publish\Markshot.exe
echo Keep the "models" folder next to the exe. Your settings live in config.json next to it
echo (created when you save Settings) or in %%APPDATA%%\Markshot\config.json.
endlocal
