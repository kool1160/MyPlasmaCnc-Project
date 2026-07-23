@echo off
setlocal

set "APPLICATION_DIRECTORY=%~dp0"
set "APPLICATION_EXE=%APPLICATION_DIRECTORY%MyPlasm Inspector.exe"
set "D2XX_DLL=%APPLICATION_DIRECTORY%native\ftd2xx.dll"
set "LAUNCHER_LOG=%APPLICATION_DIRECTORY%launcher.log"

> "%LAUNCHER_LOG%" echo [%DATE% %TIME%] MyPlasm Inspector diagnostic launcher started.

if not exist "%APPLICATION_EXE%" (
    echo ERROR: MyPlasm Inspector.exe is missing from this package.
    >> "%LAUNCHER_LOG%" echo ERROR: MyPlasm Inspector.exe is missing.
    pause
    exit /b 1
)

if not exist "%D2XX_DLL%" (
    echo ERROR: native\ftd2xx.dll is missing from this package.
    >> "%LAUNCHER_LOG%" echo ERROR: native\ftd2xx.dll is missing.
    pause
    exit /b 1
)

pushd "%APPLICATION_DIRECTORY%"
echo Starting MyPlasm Inspector. Launcher output: "%LAUNCHER_LOG%"
>> "%LAUNCHER_LOG%" echo Starting MyPlasm Inspector from "%APPLICATION_DIRECTORY%".
"%APPLICATION_EXE%" %* >> "%LAUNCHER_LOG%" 2>&1
set "APPLICATION_EXIT_CODE=%ERRORLEVEL%"
>> "%LAUNCHER_LOG%" echo Application exit code: %APPLICATION_EXIT_CODE%
popd

echo Application exit code: %APPLICATION_EXIT_CODE%
if not "%APPLICATION_EXIT_CODE%"=="0" (
    echo The application exited unexpectedly.
    echo Opening startup logs: "%LOCALAPPDATA%\MyPlasm Inspector\Logs"
    start "" explorer.exe "%LOCALAPPDATA%\MyPlasm Inspector\Logs"
)

echo Press any key to close this diagnostic launcher.
pause >nul
exit /b %APPLICATION_EXIT_CODE%
