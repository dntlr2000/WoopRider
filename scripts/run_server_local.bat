@echo off
set "ROOT=%~dp0.."
set "EXE=%ROOT%\Builds\WoopRider.exe"
set "LOGDIR=%ROOT%\Logs\ServerLogs"

if not exist "%EXE%" (
    echo Build executable not found: %EXE%
    exit /b 1
)

if not exist "%LOGDIR%" mkdir "%LOGDIR%"
for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%I"
set "LOG=%LOGDIR%\wooprider-server-%STAMP%.log"
echo Server log: %LOG%
"%EXE%" -server -port 7777 -roomId test-room-1 -maxPlayers 6 -logFile "%LOG%"
