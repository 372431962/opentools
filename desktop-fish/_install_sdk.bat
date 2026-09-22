@echo off
chcp 65001 >nul
rem ---------------------------------------------------------------
rem DesktopFish - download and install .NET 8.0 SDK.
rem Only needed to build from source; the packaged dist\DesktopFish.exe
rem runs without any SDK.
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"

set "SDK_URL=https://download.visualstudio.microsoft.com/download/pr/dc7c534c-bf24-48ca-943d-d849785c5d12/dotnet-sdk-8.0.425-win-x64.exe"
set "SDK_EXE=%TEMP%\dotnet-sdk-8.0.425.exe"

echo [DesktopFish] downloading .NET 8.0 SDK...
rem -f: fail on HTTP errors; -L: follow redirects; --retry: robustness
curl -fL --retry 3 --retry-delay 2 -o "%SDK_EXE%" "%SDK_URL%"
if errorlevel 1 (
    echo [DesktopFish] download FAILED ^(network error or bad HTTP status^).
    goto :fail
)

rem installer is ~200MB; a much smaller file means an error page or truncation
for %%A in ("%SDK_EXE%") do set "SDK_SIZE=%%~zA"
if not defined SDK_SIZE (
    echo [DesktopFish] could not read download size.
    goto :fail
)
if %SDK_SIZE% LSS 100000000 (
    echo [DesktopFish] download too small ^(%SDK_SIZE% bytes^), likely truncated.
    goto :fail
)

echo [DesktopFish] verifying file is readable...
certutil -hashfile "%SDK_EXE%" SHA512 >nul 2>nul
if errorlevel 1 (
    echo [DesktopFish] warning: could not hash the file, continuing.
) else (
    echo [DesktopFish] hash computed ^(not compared to official value^).
)

echo [DesktopFish] installing .NET 8.0 SDK ^(may take a few minutes^)...
"%SDK_EXE%" /quiet /norestart /install

rem 0 = success; 3010 = success but reboot required; anything else = failure
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto :ok
if "%RC%"=="3010" (
    echo [DesktopFish] installed, but a reboot is required.
    pause
    exit /b 0
)
echo [DesktopFish] install FAILED, installer exit code: %RC%
goto :fail

:ok
echo [DesktopFish] install complete. Reopen this window, then run publish.bat.
pause
exit /b 0

:fail
echo [DesktopFish] could not auto-install. Please install .NET 8.0 SDK manually:
echo   https://dotnet.microsoft.com/download/dotnet/8.0
pause
exit /b 1
