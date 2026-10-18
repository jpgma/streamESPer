@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PORT="
set "MONITOR="

:parse
if "%~1"=="" goto run
if /I "%~1"=="--help" goto help
if /I "%~1"=="-h" goto help
if /I "%~1"=="/?" goto help
if /I "%~1"=="--monitor" (
    set "MONITOR=1"
    shift
    goto parse
)
if /I "%~1"=="-Monitor" (
    set "MONITOR=1"
    shift
    goto parse
)
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
echo Grava o firmware w154 e volta ao prompt.
echo --monitor abre o serial numa janela propria. Ctrl+C nela encerra.
echo.
echo   flash.bat
echo   flash.bat --port COM7
echo   flash.bat COM7
echo   flash.bat --monitor
echo   flash.bat --monitor --port COM7
exit /b 0

:help_err
call :help
exit /b 1

:run
set "EXTRA="
if defined MONITOR set "EXTRA=-Monitor"
if defined PORT (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firmware\flash.ps1" -Port "%PORT%" %EXTRA%
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firmware\flash.ps1" %EXTRA%
)
exit /b %ERRORLEVEL%
