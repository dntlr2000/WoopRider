@echo off
set "ROOT=%~dp0.."
set "EXE=%ROOT%\Builds\WoopRider.exe"
set "LOG=%ROOT%\Builds\client-1.log"

if not exist "%EXE%" (
    echo Build executable not found: %EXE%
    exit /b 1
)

start "WoopRider Client" "%EXE%" -logFile "%LOG%"
