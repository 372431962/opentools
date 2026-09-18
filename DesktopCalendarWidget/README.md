# DesktopCalendarWidget · 桌面日历挂件

面向 Windows 10/11 x64 的桌面月历挂件，使用 .NET 8 WPF 开发，无第三方 NuGet 包依赖。项目托管于 [opentools](https://github.com/372431962/opentools)，专注于日历展示。

## 功能

- 公历月历、上/下月切换、回到今天；双击日期回到本月。
- 农历月日、闰月及干支年份显示。
- 节假日与调休标记，支持本地编辑和手动联网更新。
- 不标记、每周单休、隔周单休（大小周）三种休息日模式。
- 无边框窗口，支持拖动、位置锁定、透明度、置顶和鼠标穿透。
- 系统托盘、全局快捷键、可选随 Windows 启动；任务栏不显示程序按钮。
- 单实例运行，重复启动会显示已有挂件。

## 下载与使用

1. 前往 [最新版本下载页](https://github.com/372431962/opentools/releases/latest)，选择 `DesktopCalendarWidget-<版本>-win-x64.msi`。
2. 在 Windows 10/11 x64 上双击 MSI 安装。安装包包含 .NET 运行时，无需另装 .NET SDK。
3. 从桌面或开始菜单的「桌面日历」快捷方式启动，通过挂件右上角「⋯」或托盘菜单打开设置。

安装目录为 `%LocalAppData%\Programs\DesktopCalendarWidget`。可在 Windows「设置 → 应用」中卸载。

托盘右键菜单提供「设置」「隐藏挂件 / 显示挂件」「退出」；双击托盘图标切换显示与隐藏。鼠标穿透开启后，仍可通过托盘进入设置。

| 快捷键 | 操作 |
| --- | --- |
| `Ctrl+Alt+C` | 切换鼠标穿透 |
| `Ctrl+Alt+L` | 切换位置锁定 |

快捷键可能被其他程序占用。如果启动时鼠标穿透已开启且 `Ctrl+Alt+C` 注册失败，程序会关闭穿透并提示。

## 休息日与隔周单休

在设置的「休息日」区域选择模式：

| 模式 | 规则 |
| --- | --- |
| 不标记 | 周六、周日按普通周末显示，不添加排班「休」标记 |
| 每周单休 | 每周只休所选的周六或周日 |
| 隔周单休（大小周） | 单休周和双休周逐周交替，单休周休所选的一天，双休周休周六、周日 |

配置大小周时，选择单休日，并用「本周是单休周」指定本周状态：勾选表示单休，不勾选表示双休。一周以**周一**为起点；首次设置或修改本周状态时，将本周周一记为锚点，向前、向后按周交替推算。仅修改透明度等无关选项不会重设已有锚点。

配色与标记：

- **红色**：不标记模式下的周末，或排班模式下的休息日，以及数据中标为放假的日期。
- **橙色「班」**：数据中标为调休上班的日期，优先于休息日规则。
- **「休」**：排班休息日；有节假日名称时优先显示名称。
- 单休周需要上班的周末日期显示普通颜色，节假日数据另有标记时除外。

节假日和调休标记需开启设置中的节假日显示选项。

## 节假日数据与手动更新

**当前内置基础数据只有 33 条，不等同于完整年度放假调休安排。** 2025 年数据不完整且缺少调休上班日，2026 年仅包含节日当天。使用前请更新，并按官方公布的放假调休安排核对；内置数据不覆盖未来多年。

在设置中手动触发联网更新，程序会依次请求当前年和下一年的数据。默认接口为：

```text
https://timor.tech/api/holiday/year/{0}
```

- `{0}` 为年份占位符，自定义地址必须保留它，并提供程序可识别的日期、名称及调休字段。
- 程序不会在后台自动联网更新。接口不可用时继续使用已有本地数据。
- 更新按日期合并：同日记录被替换，接口未返回的旧记录会保留。更新成功也不代表年度数据完整，应核对并修正遗漏或旧记录。
- 下载成功的数据立即保存到本地；更新地址在点击「保存」后写入配置。

设置中的「本地数据编辑」支持**每行一个 JSON 对象**。以下仅演示格式，不代表完整放假安排：

```json
{"Date":"2026-10-01","Name":"国庆节","IsWorkday":false,"IsHoliday":true}
```

日期使用 `yyyy-MM-dd`；调休上班日设置 `IsWorkday: true`、`IsHoliday: false`。编辑后点击「保存本地节假日数据」或「保存」。

## 数据目录

配置和节假日保存在 `%LocalAppData%\DesktopCalendarWidget`，可通过设置中的「打开数据目录」访问。

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 窗口位置、透明度、休息日配置、更新地址等 |
| `holidays.json` | 节假日与调休记录，文件格式为 JSON 数组 |

直接编辑文件前建议退出程序并备份；注意 `holidays.json` 的数组格式与设置编辑框的逐行格式不同。

## 源码结构

以下路径均相对于本 README 所在目录；项目放在 `opentools/DesktopCalendarWidget` 子目录时同样适用。

```text
DesktopCalendarWidget.csproj    .NET 8 WPF 工程
App.xaml / App.xaml.cs         应用资源与启动入口
MainWindow.xaml / MainWindow.xaml.cs          日历界面与窗口交互
SettingsWindow.xaml / SettingsWindow.xaml.cs  设置界面与手动更新入口
Models.cs                     配置与节假日数据模型
RestSchedule.cs               单休、大小周推算
LunarCalendarConverter.cs     农历转换
SettingsService.cs            本地数据读写及内置基础数据
HolidayService.cs             节假日下载与解析
TrayIcon.cs / SingleInstance.cs 托盘与单实例支持
StartupService.cs             随 Windows 启动
Assets/app.ico                应用图标
installer/                    WiX 定义与打包脚本
LICENSE                       MIT 许可证
```

## 构建与打包

需要 Windows 和 .NET 8 SDK。在包含 `DesktopCalendarWidget.csproj` 的目录执行；若从 opentools 仓库根目录开始，先运行 `cd DesktopCalendarWidget`。

```powershell
dotnet build .\DesktopCalendarWidget.csproj -c Release
dotnet run --project .\DesktopCalendarWidget.csproj
```

生成 **1.3.0** 的 Windows x64 安装包，使用 **WiX 5.0.2**：

```powershell
dotnet tool install --global wix --version 5.0.2
powershell -File .\installer\build-installer.ps1 -DotNet dotnet -Version 1.3.0
```

运行前确保 `dotnet` 和 `wix` 命令可用。脚本先执行自包含单文件发布，再生成 MSI，产物为：

```text
artifacts\DesktopCalendarWidget-1.3.0-win-x64.msi
```

可双击安装，或执行：

```powershell
msiexec /i "artifacts\DesktopCalendarWidget-1.3.0-win-x64.msi"
```

发布新版本时递增 `-Version`；脚本将该版本同时传给应用发布和 MSI 打包。

### GitHub Release 自动发布

仓库的 `.github/workflows/release.yml` 在推送 `v1.3.0` 这类版本标签时，使用 Windows 构建器生成 MSI 和 `SHA256SUMS.txt`，并创建对应 Release。标签中的版本号会传入打包脚本；上传成功后才公开 Release。发布说明取自本目录的 `RELEASE_NOTES.md`，后续发布前应同步更新。

工作流也支持手动运行并指定已有标签，便于重试失败的构建。需要仓库启用 GitHub Actions；发布使用工作流自带的 `GITHUB_TOKEN`，不需要把个人令牌保存在源码中。

## 已知限制

- 仅面向 Windows；当前打包脚本生成 x64 安装包。
- 农历依赖 `ChineseLunisolarCalendar`，支持 1901-02-19 至 2101-01-28，超出范围不显示农历。
- 内置节假日数据不完整，公开更新接口的可用性和数据完整性也不作保证。
- 隔周单休固定以周一为周界交替，不支持按月指定周次等复杂排班。
- 全局快捷键可能冲突，可使用托盘菜单操作。

构建、安装升级和界面交互需在目标 Windows 环境中验证；本说明不将历史本机检查视为全面兼容性保证。

## 许可证

采用 [MIT License](LICENSE)，允许在保留许可证与版权声明的前提下使用、修改和分发。
