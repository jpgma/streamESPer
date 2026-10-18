@echo off
setlocal
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
start "W154Stream" /D "%CD%" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\w154stream.ps1" -RepoRoot "%ROOT%" %*
exit /b 0
