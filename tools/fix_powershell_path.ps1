<#
    fix_powershell_path.ps1
    ------------------------------------------------------------------
    Problem: a host process spawns the BARE name "powershell" and fails
             with ENOENT, because its PATH does not contain
                %SystemRoot%\System32\WindowsPowerShell\v1.0
             (that folder is where the real powershell.exe lives; the
              Start Menu entry is only a .lnk shortcut).

    This script appends the missing folder(s) to the USER PATH,
    preserves the original registry value type, broadcasts
    WM_SETTINGCHANGE, and prints what to do next.

    Usage:
        fix_powershell_path.bat                 (double-click; no admin)
        fix_powershell_path.bat -Machine        (needs "Run as admin")

    Messages are kept ASCII on purpose: Windows PowerShell 5.1 reads
    .ps1 files without a BOM using the ANSI codepage.
#>
[CmdletBinding()]
param([switch]$Machine)

$ErrorActionPreference = 'Stop'

$systemRoot = $env:SystemRoot
$target = Join-Path $systemRoot 'System32\WindowsPowerShell\v1.0'
$system32 = Join-Path $systemRoot 'System32'
$exe = Join-Path $target 'powershell.exe'

$userKey = 'HKCU:\Environment'
$machineKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$key = if ($Machine) { $machineKey } else { $userKey }

Write-Host ''
Write-Host "=== PowerShell PATH repair ===" -ForegroundColor Cyan
Write-Host "[1/4] target folder : $target"
if (Test-Path $exe) {
    Write-Host "      powershell.exe : found" -ForegroundColor Green
} else {
    Write-Host "      powershell.exe : NOT FOUND - Windows PowerShell may be removed on this machine." -ForegroundColor Yellow
}

function Read-PathValue([string]$pathKey) {
    $item = Get-Item -Path $pathKey
    if ($item.GetValueNames() -notcontains 'Path') {
        return [pscustomobject]@{ Text = ''; Kind = 'ExpandString' }
    }
    $raw = [string]$item.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    return [pscustomobject]@{ Text = $raw; Kind = $item.GetValueKind('Path').ToString() }
}

$current = Read-PathValue $key
Write-Host "[2/4] scope : $key   (value type: $($current.Kind))"
$rawParts = @($current.Text -split ';' | Where-Object { $_.Trim() -ne '' })

# compare using expanded values, but write back the original raw text
$expandedParts = @($rawParts | ForEach-Object { [System.Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') })
$wanted = @($target, $system32)
$missing = @()
foreach ($w in $wanted) {
    $needle = $w.TrimEnd('\')
    $found = $false
    foreach ($p in $expandedParts) {
        if ($p -ieq $needle) { $found = $true; break }
    }
    if (-not $found) { $missing += $w }
}

if ($missing.Count -eq 0) {
    Write-Host "[3/4] nothing to add: both folders are already on this PATH." -ForegroundColor Green
} else {
    if ($Machine) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Writing the machine (system) PATH requires an elevated window: right-click the .bat -> Run as administrator.'
        }
    }
    $newText = (@($rawParts) + $missing) -join ';'
    Set-ItemProperty -Path $key -Name Path -Value $newText -Type $current.Kind
    Write-Host "[3/4] added to PATH:" -ForegroundColor Green
    foreach ($m in $missing) { Write-Host "      + $m" }
}

Write-Host "[4/4] broadcasting WM_SETTINGCHANGE so new processes pick it up ..."
Add-Type -Namespace PInvoke -Name User32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
'@
$out = [IntPtr]::Zero
[void][PInvoke.User32]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [IntPtr]::Zero, 'Environment', 0x0002, 5000, [ref]$out)

$verify = (Read-PathValue $key).Text
Write-Host ''
Write-Host "=== current $key \ Path ===" -ForegroundColor Cyan
Write-Host $verify
Write-Host ''
Write-Host "=== NEXT STEPS ===" -ForegroundColor Cyan
Write-Host " 1) Open a NEW cmd window and run:  where powershell"
Write-Host "    expected output: $exe"
Write-Host " 2) FULLY quit your editor / agent host (tray exit or kill all Code.exe),"
Write-Host "    then start it again from the Start Menu - only a window reload is NOT enough."
Write-Host " 3) Retry the agent command. If a NEW cmd finds powershell.exe but the host"
Write-Host "    still fails, the host passes its own stripped environment: then configure"
Write-Host "    the host's shell path explicitly (absolute path to powershell.exe)."
Write-Host ''
