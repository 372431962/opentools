# 构建 Windows 安装包（MSI）
#
# 用法（在装有 .NET 8 SDK 的机器上）：
#   dotnet tool install --global wix --version 5.0.2
#   pwsh -File .\build-installer.ps1 -DotNet dotnet
#
# 产物：..\artifacts\DesktopCalendarWidget-1.3.0-win-x64.msi

param(
    [string]$DotNet = 'dotnet',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.3.0'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'DesktopCalendarWidget.csproj'
$artifacts = Join-Path $projectRoot 'artifacts'
$publish = Join-Path $artifacts 'publish'
$msi = Join-Path $artifacts ("DesktopCalendarWidget-$Version-$Runtime.msi")

if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

Write-Output '== publishing self-contained single-file app =='
& $DotNet publish $project -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true /p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $publish 'LICENSE') -Force

Write-Output '== building MSI with WiX =='
$previousRollForward = $env:DOTNET_ROLL_FORWARD
try {
    $env:DOTNET_ROLL_FORWARD = 'Major'
    & wix build (Join-Path $PSScriptRoot 'product.wxs') -bindpath "publish=$publish" -o $msi -arch x64 -d Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "wix build 失败，退出码 $LASTEXITCODE" }
}
finally {
    $env:DOTNET_ROLL_FORWARD = $previousRollForward
}

Write-Output ("MSI 已生成：" + $msi)
Write-Output '安装：msiexec /i "<msi 路径>"   卸载：msiexec /x "<msi 路径>"'
