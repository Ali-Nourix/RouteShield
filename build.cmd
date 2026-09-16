@echo off
setlocal
rem Builds artifacts\RouteShield-<version>-win-x64.zip. Run this from a normal CMD window.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" %*
exit /b %errorlevel%
