# OpenTools

基于 AI 辅助开发的开源小工具，专注简单、实用的桌面体验。

## 桌面日历 · DesktopCalendarWidget

适用于 Windows 10 / 11 x64 的桌面月历挂件，显示公历、农历、节假日与休息日，无账号、广告、待办或日程管理。

**[下载安装包](https://github.com/372431962/opentools/releases/latest)** · **[使用与构建说明](DesktopCalendarWidget/README.md)** · **[源码](DesktopCalendarWidget/)**

- **月历展示**：月份切换、今日高亮、农历日期和干支年份。
- **作息配置**：每周单休、隔周单休（大小周）；设置本周状态，以周一为界推算。周末调休改变实际单/双休时，后续周次自动顺延，按实际休息日着色。
- **节假日数据**：内置基础数据，支持本地编辑与手动联网更新。内置数据不是完整的年度放假调休表，使用前请更新并核对。
- **桌面挂件**：可拖动、锁定、调透明度、鼠标穿透；托盘常驻，任务栏不显示挂件按钮。
- **单实例**：重复启动会唤起已有挂件；双击托盘图标显示或隐藏挂件。

## 安装与使用

1. 从 [Releases](https://github.com/372431962/opentools/releases) 下载 `DesktopCalendarWidget-1.3.1-win-x64.msi`。
2. 双击安装，从桌面或开始菜单打开「桌面日历」。安装包包含 .NET 运行时，无需另装 SDK。
3. 右键系统托盘图标 →「设置」，配置单休日、大小周和显示方式；「退出」可结束程序。

托盘图标可能收纳在通知区域的 `^` 中。`Ctrl+Alt+C` 切换鼠标穿透，`Ctrl+Alt+L` 切换位置锁定。

安装包目前未做代码签名，Windows 可能显示未知发布者提示。Release 附带 `SHA256SUMS.txt`，可用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 核对下载文件。

## 开发

源码使用 **C# / .NET 8 / WPF**，MSI 使用 **WiX 5.0.2** 构建。

```powershell
git clone git@github.com:372431962/opentools.git
cd opentools\DesktopCalendarWidget
dotnet build .\DesktopCalendarWidget.csproj -c Release
dotnet run --project .\DesktopCalendarWidget.csproj
```

打包命令、数据格式、功能限制见 [项目 README](DesktopCalendarWidget/README.md)。安装包通过 Release 分发，仓库仅保存源码和必要资源。

## 许可证

采用 [MIT License](LICENSE)。欢迎通过 [Issues](https://github.com/372431962/opentools/issues) 提交问题和建议。
