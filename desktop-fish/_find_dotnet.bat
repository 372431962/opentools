@echo off
chcp 65001 >nul
rem ---------------------------------------------------------------
rem Locate a usable dotnet CLI.
rem   Only accept dotnet.exe, so a dotnet.cmd / dotnet.bat in the
rem   current folder cannot hijack the launcher chain.
rem Called via "call" from start_fish.bat / run.bat / publish.bat.
rem Output: DOTNET = full path of dotnet.exe (undefined if not found)
rem ---------------------------------------------------------------

set "DOTNET="

rem 1) known SDK install locations first (absolute paths, before PATH)
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LocalAppData%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LocalAppData%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LocalAppData%\dotnet\dotnet.exe" set "DOTNET=%LocalAppData%\dotnet\dotnet.exe"

rem 2) then PATH, accepting only .exe
if not defined DOTNET for /f "delims=" %%P in ('where dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%P"

exit /b 0
