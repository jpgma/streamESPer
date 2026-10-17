@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%W154Convert\bin\Release\net10.0\w154.exe"

echo Compilando W154Convert Release...
dotnet build "%ROOT%W154Convert\W154Convert.csproj" -c Release --nologo -v q
if errorlevel 1 exit /b 1

"%EXE%" %*
exit /b %ERRORLEVEL%
