@echo off
chcp 65001 >nul
rem ---------------------------------------------------------------
rem DesktopFish - build and pack into a single-file EXE.
rem Requires .NET 8.0 SDK. Run _install_sdk.bat first if missing.
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"

call "_find_dotnet.bat"

if not defined DOTNET (
    echo [DesktopFish] .NET SDK not found.
    echo   Run _install_sdk.bat to install .NET 8.0 SDK first.
    pause
    exit /b 1
)

echo [DesktopFish] building...
%DOTNET% publish DesktopFish.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false /p:EnableCompressionInSingleFile=true -o dist

if errorlevel 1 (
    echo [DesktopFish] build FAILED.
    pause
    exit /b 1
)

echo [DesktopFish] build OK.
echo [DesktopFish] output: dist\DesktopFish.exe
echo [DesktopFish] run it directly, no .NET installation required.
pause
