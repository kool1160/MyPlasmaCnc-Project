@echo off
setlocal

set "REPOSITORY_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPOSITORY_ROOT%scripts\Build-PortableInspector.ps1"
if errorlevel 1 (
    echo.
    echo Portable package build failed. Read the error above, then press any key to close.
    pause >nul
    exit /b 1
)

echo.
echo Portable package build complete. Press any key to close.
pause >nul
exit /b 0
