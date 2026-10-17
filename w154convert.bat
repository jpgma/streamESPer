@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%W154Convert\bin\Release\net10.0\w154.exe"

if not exist "%EXE%" (
  echo Compilando W154Convert Release...
  dotnet build "%ROOT%W154Convert\W154Convert.csproj" -c Release --nologo
  if errorlevel 1 exit /b 1
)

"%EXE%" %*
exit /b %ERRORLEVEL%
