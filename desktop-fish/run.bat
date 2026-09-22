@echo off
chcp 65001 >nul
rem ---------------------------------------------------------------
rem DesktopFish verbose launcher: keeps the console and shows logs.
rem   run.bat                 start (ESC to quit)
rem   run.bat --restore       only restore desktop icons
rem   run.bat --speed 0.7 --fps 30
rem   Prefers the packaged dist\DesktopFish.exe (needs no SDK).
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"
title DesktopFish - verbose

if exist "dist\DesktopFish.exe" (
    "dist\DesktopFish.exe" %*
    set EXITCODE=%ERRORLEVEL%
    echo.
    if not "%EXITCODE%"=="0" echo [DesktopFish] exit code %EXITCODE%
    pause
    exit /b %EXITCODE%
)

call "_find_dotnet.bat"

if not defined DOTNET (
    echo [DesktopFish] .NET SDK not found and dist\DesktopFish.exe is missing.
    echo   Install .NET 8.0+ or run publish.bat first.
    echo   https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

%DOTNET% run --project DesktopFish.csproj %*
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo [DesktopFish] exit code %EXITCODE%
pause
exit /b %EXITCODE%
