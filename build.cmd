@echo off
setlocal
rem One-step build. Double-click this file, or run it from a CMD window.
rem Output: artifacts\RouteShield-<version>-win-x64.zip
rem
rem Nothing needs to be installed first. If the .NET SDK is missing, the build
rem fetches a private copy into .tools\dotnet and leaves the rest of your machine alone.
rem
rem Options are passed straight through, for example:
rem   build.cmd -Runtime win-arm64
rem   build.cmd -Version 1.5.1

echo.
echo   RouteShield build
echo   -----------------
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" %*
set EXITCODE=%errorlevel%

if not "%EXITCODE%"=="0" (
    echo.
    echo   The build failed with exit code %EXITCODE%. The messages above say where.
)

rem Keep the window open when the script was started by double-clicking.
echo %cmdcmdline% | find /i "/c" >nul
if not errorlevel 1 pause

exit /b %EXITCODE%
