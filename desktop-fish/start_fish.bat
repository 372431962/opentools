@echo off
chcp 65001 >nul
rem ---------------------------------------------------------------
rem DesktopFish one-click launcher (daily use).
rem   Double-click this file; press ESC to quit and restore icons.
rem   Prefers the packaged dist\DesktopFish.exe (needs no SDK),
rem   otherwise falls back to "dotnet run" (needs .NET SDK).
rem   Use run.bat if you want to see logs and errors.
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"
title DesktopFish

if exist "dist\DesktopFish.exe" (
    "dist\DesktopFish.exe" %*
    exit /b %ERRORLEVEL%
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
exit /b %ERRORLEVEL%
