@echo off
chcp 65001 >nul
rem ---------------------------------------------------------------
rem 双击本文件即可修复"宿主找不到裸名字 powershell"的问题
rem   - 只修改【用户 PATH】（HKCU\Environment），不需要管理员
rem   - 修复后必须"完全退出"编辑器/agent 宿主再重开（重载窗口无效）
rem   - 想改系统 PATH：右键本文件 -> 以管理员身份运行，并传 -Machine
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

echo [fix_powershell_path] 开始修复...
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0fix_powershell_path.ps1" %*

echo.
echo [fix_powershell_path] 完成后请：新开一个 cmd 执行  where powershell  验证；
echo                然后完全退出并重新打开编辑器/agent 宿主。
pause
