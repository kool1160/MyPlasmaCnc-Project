@echo off
setlocal

set "APPLICATION_DIRECTORY=%~dp0"
set "APPLICATION_EXE=%APPLICATION_DIRECTORY%MyPlasm Inspector.exe"
set "D2XX_DLL=%APPLICATION_DIRECTORY%native\ftd2xx.dll"

if not exist "%APPLICATION_EXE%" (
    echo ERROR: MyPlasm Inspector.exe is missing from this package.
    echo Extract the complete ZIP before launching it.
    pause
    exit /b 1
)

if not exist "%D2XX_DLL%" (
    echo ERROR: native\ftd2xx.dll is missing from this package.
    echo Extract the complete ZIP before launching it.
    pause
    exit /b 1
)

start "" /d "%APPLICATION_DIRECTORY%" "%APPLICATION_EXE%"
if errorlevel 1 (
    echo ERROR: MyPlasm Inspector could not be started.
    pause
    exit /b 1
)

exit /b 0
