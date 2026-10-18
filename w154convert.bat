@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%W154Convert\bin\Release\net10.0\w154.exe"

if not exist "%EXE%" (
  echo Rode build.bat --converter antes de iniciar o conversor.
  exit /b 1
)

"%EXE%" %*
exit /b %ERRORLEVEL%
