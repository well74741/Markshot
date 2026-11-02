@echo off
rem Builds dist\Markshot-Setup-<version>.exe (see build_installer.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_installer.ps1"
if errorlevel 1 pause
