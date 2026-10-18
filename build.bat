@echo off
setlocal
set "ROOT=%~dp0"
set "DO_CONVERTER=0"
set "DO_API=0"

if "%~1"=="" (
  set "DO_CONVERTER=1"
  set "DO_API=1"
  goto build
)

:parse
if "%~1"=="" goto build
if /I "%~1"=="--converter" (
  set "DO_CONVERTER=1"
  shift
  goto parse
)
if /I "%~1"=="--api" (
  set "DO_API=1"
  shift
  goto parse
)
echo Flag desconhecida: %~1
exit /b 1

:build
if "%DO_CONVERTER%"=="1" (
  echo Compilando W154Convert Release...
  dotnet build "%ROOT%W154Convert\W154Convert.csproj" -c Release --nologo -v q
  if errorlevel 1 exit /b 1
)
if "%DO_API%"=="1" (
  echo Compilando W154Stream Release...
  dotnet build "%ROOT%W154Stream\W154Stream.csproj" -c Release --nologo -v q
  if errorlevel 1 exit /b 1
)
exit /b 0
