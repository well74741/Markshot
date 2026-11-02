@echo off
rem Markshot 打包：检查依赖并生成 dist\ 下的便携版 zip 和安装包。参数见 package.ps1。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1" %*
if errorlevel 1 pause
