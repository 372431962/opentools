# OpenTools

基于 AI 辅助开发的开源小工具，专注简单、实用的桌面体验。

- **[桌面日历 DesktopCalendarWidget](DesktopCalendarWidget/)** —— 显示公历、农历、节假日与休息日的桌面月历挂件。
- **[桌面小鱼 DesktopFish](desktop-fish/)** —— 让桌面上所有图标像小鱼一样游动，并能直接点开它们。
- **[tools/](tools/)** —— 环境修复脚本（PowerShell 路径等）。

## 桌面日历 · DesktopCalendarWidget

适用于 Windows 10 / 11 x64 的桌面月历挂件，显示公历、农历、节假日与休息日，无账号、广告、待办或日程管理。

**[下载安装包](https://github.com/372431962/opentools/releases/latest)** · **[使用与构建说明](DesktopCalendarWidget/README.md)** · **[源码](DesktopCalendarWidget/)**

- **月历展示**：月份切换、今日高亮、农历日期和干支年份。
- **界面语言**：简体中文与英文，在设置中切换并可立即重启生效；英文界面不显示农历和中国节假日文字。
- **作息配置**：每周单休、隔周单休（大小周）；设置本周状态，以周一为界推算。周末调休改变实际单/双休时，后续周次自动顺延，按实际休息日着色。
- **节假日数据**：内置基础数据，支持本地编辑与手动联网更新。内置数据不是完整的年度放假调休表，使用前请更新并核对。
- **每日天气**：每个日期显示天气图标与最高/最低温度，悬停看天气现象、温差与降水概率。
- **中英互译小窗**：自动识别方向、双向互译，可置顶到桌面所有窗口之上。
- **自动检查更新**：启动后与每 12 小时查询一次最新 Release，发现新版可一键下载安装包并拉起安装。
- **桌面挂件**：无边框透明，可拖动、锁定、置顶和鼠标穿透；天气动画直接展示在桌面上。
- **单实例**：重复启动会唤起已有挂件；双击托盘图标显示或隐藏挂件。

## 安装与使用 · 桌面日历

1. 从 [Releases](https://github.com/372431962/opentools/releases) 下载最新版的 `DesktopCalendarWidget-<版本>-win-x64.msi`。
2. 双击安装，从桌面或开始菜单打开「桌面日历」。安装包包含 .NET 运行时，无需另装 SDK。
3. 右键系统托盘图标 →「设置」，配置单休日、大小周和显示方式；「退出」可结束程序。

托盘图标可能收纳在通知区域的 `^` 中。`Ctrl+Alt+C` 切换鼠标穿透，`Ctrl+Alt+L` 切换位置锁定。

安装包目前未做代码签名，Windows 可能显示未知发布者提示。Release 附带 `SHA256SUMS.txt`，可用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 核对下载文件。

## 桌面小鱼 · DesktopFish

适用于 Windows 10 / 11 x64 的桌面玩具：启动后桌面上**每一个图标**（快捷方式、文件夹、此电脑、回收站……）
都会从原来的位置"活过来"，摆着尾巴游来游去；按 `ESC` 退出后图标回到原位。

**[使用与构建说明](desktop-fish/README.md)** · **[源码](desktop-fish/desktop_fish.cs)**

- **零配置**：不需要任何图片素材，小鱼是启动时用 `PrintWindow` 从桌面图标层现采的。
- **不改动真实图标**：动画期间真实图标只是被隐藏（explorer 刷新把它抖出来时会立刻再隐藏一次），
  退出必定恢复；万一没恢复，双击 `desktop-fish/restore_icons.vbs` 或运行 `DesktopFish.exe --restore`。
- **可以真的点**：光标停在某条小鱼上，它会停在原地继续摆尾；双击打开那个图标，
  右键弹出资源管理器给该图标的**原生菜单**（重命名 / 属性 / 删除都在）。
- **不打扰操作**：画面放在壁纸之上、普通窗口之下，窗口点击穿透；`--top` 可改成浮在所有窗口之上。
- **可调**：`--speed` 速度倍率、`--fps` 帧率、`--angles` 预渲染角度、`--no-mouse` 交还鼠标、
  `--probe` 自检、`--debug` / `--opaque` 排查。

运行：双击 `desktop-fish/start_fish.bat`（后台运行、无控制台窗口），或构建后直接跑
`desktop-fish/dist/DesktopFish.exe`。右键桌面 →「查看」→「显示桌面图标」被取消勾选时检不到任何图标；
桌面被最大化窗口完全盖住时看不到效果（按 `Win+D`，或改用 `--top`）。

## 环境脚本 · tools

- [`tools/fix_powershell_path.ps1`](tools/fix_powershell_path.ps1)：把 `System32\WindowsPowerShell\v1.0` 等目录追加进
  **用户** `PATH`（保留原注册表值类型、广播 `WM_SETTINGCHANGE`），解决某些宿主进程派生终端时
  找不到裸名字 `powershell`（`ENOENT`）的问题；加 `-Machine` 并"以管理员身份运行"则改系统 `PATH`。
  改完必须**完全退出**编辑器 / agent 宿主再重开，重载窗口无效。同目录的 `.bat` 是双击运行的包装。
- [`.vscode/settings.json`](.vscode/settings.json)：同一问题的编辑器侧兜底 —— 用绝对路径启动 PowerShell，
  并给集成终端的 `PATH` 追加系统目录。

## 开发

源码统一使用 **C# / .NET 8**。

### 桌面日历

WPF 实现，MSI 使用 **WiX 5.0.2** 构建。

```powershell
git clone git@github.com:372431962/opentools.git
cd opentools\DesktopCalendarWidget
dotnet build .\DesktopCalendarWidget.csproj -c Release
dotnet run --project .\DesktopCalendarWidget.csproj
```

打包命令、数据格式、功能限制见 [项目 README](DesktopCalendarWidget/README.md)。安装包通过 Release 分发，仓库仅保存源码和必要资源。

### 桌面小鱼

WinForms + P/Invoke，整个程序是一个 `.cs` 文件；构建需要 .NET 8 SDK，运行不需要任何运行时。

```powershell
cd opentools\desktop-fish
dotnet run --project .\DesktopFish.csproj -- --probe    # 只打印检到的图标、名称与位置
dotnet publish .\DesktopFish.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
```

`dist/` 是构建产物（约 70 MB 单文件 exe），已在 `.gitignore` 里忽略；`publish.bat` 包装了同一条命令。
完整参数表、工作原理与各 Windows 版本差异见 [项目 README](desktop-fish/README.md)。

## 许可证

采用 [MIT License](LICENSE)。欢迎通过 [Issues](https://github.com/372431962/opentools/issues) 提交问题和建议。
