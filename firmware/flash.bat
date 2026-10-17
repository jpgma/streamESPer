@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PORT="

:parse
if "%~1"=="" goto run
if /I "%~1"=="--help" goto help
if /I "%~1"=="-h" goto help
if /I "%~1"=="/?" goto help
if /I "%~1"=="--port" goto take_port
if /I "%~1"=="-Port" goto take_port
echo %~1| findstr /r /i "^COM[0-9][0-9]*$" >nul
if not errorlevel 1 (
    set "PORT=%~1"
    shift
    goto parse
)
echo Argumento desconhecido: %~1
echo.
goto help_err

:take_port
shift
if "%~1"=="" (
    echo Falta a porta depois de --port.
    echo.
    goto help_err
)
set "PORT=%~1"
shift
goto parse

:help
echo Grava o firmware w154 e abre o monitor.
echo.
echo   flash.bat
echo   flash.bat --port COM7
echo   flash.bat COM7
exit /b 0

:help_err
call :help
exit /b 1

:run
if defined PORT (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0flash.ps1" -Port "%PORT%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0flash.ps1"
)
exit /b %ERRORLEVEL%
