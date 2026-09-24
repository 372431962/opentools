# DesktopCalendarWidget 功能代码评审

评审范围：`DesktopCalendarWidget/` 下全部生产源码（约 4200 行，25 个 .cs 文件）与 `tests/` 两套回归工程。
版本基线：`DesktopCalendarWidget.csproj` 的 `<Version>1.5.0</Version>`。
评审方式：静态阅读 + 逻辑推演，**未在本机编译与实机运行**，标注「需实机验证」的条目请在目标 Windows 环境确认。

---

## 修复记录（2026-09-23）

全部 23 条已处置，其中 21 条按建议修改，2 条经复核后保留原实现。

| 编号 | 状态 | 改动要点 |
| --- | --- | --- |
| A1 | 已修 | `App.xaml.cs` 改 `initiallyOwned: false` + `WaitOne(TimeSpan.Zero)`，捕获 `AbandonedMutexException` 并按首个实例继续 |
| A2 | 已修 | 设置窗口改为编辑 `settings.Clone()` 副本；新增 `HolidaysPersisted`，节假日即时落盘后主窗口无论保存或取消都同步内存 |
| A3 | 已修 | `UpdateService` 下载后先比对字节数，再用 `SHA256SUMS.txt` 校验 SHA256；解析 `assets` 不再提前 `break`（原来会漏掉校验文件）；校验失败删除安装包并提示 |
| A4 | 已修 | `DownloadDialog` 的 `Closing` 事件触发 `Cancellation.Cancel()`，关闭窗口等同取消 |
| A5 | 已修 | `WeatherService` 由主窗口持有并注入设置窗口；刷新加 `SemaphoreSlim` 串行化，缓存读写改异步 |
| B1 | 保留 | 复核后确认 `OpenSettings` 里 `Opacity` 赋值本就先于 `ApplyClickThrough`，改动点不成立；`WS_EX_LAYERED` 保留（去掉后穿透有失效风险），仍建议实机验证「改透明度后穿透是否仍生效」 |
| B2 | 已修 | `HolidayService` 请求带上统一 User-Agent |
| B3 | 已修 | `TranslateCache` 增加常驻磁盘索引，未命中不再读文件反序列化 |
| B4 | 已修 | 天气缓存改 `ReadAllTextAsync` / `WriteAllTextAsync` |
| B5 | 已修 | 节假日同日去重改为按 `HolidayEntry.SchedulePriority` 取（与 `CreateHolidayMap` 一致） |
| B6 | 已修 | `WeatherService.IsStale` 标记旧数据，图例行显示「天气为上次数据」 |
| B7 | 已修 | 启动时先建 `WeatherService` 并 `LoadFromCache()`，首屏即有天气；刷新后仅在快照引用变化时重绘 |
| B8 | 已修 | `WidgetSettings` 增加 `Width` / `Height`，启动时应用（带 clamp），关闭时保存 |
| B9 | 已修 | 两个全局快捷键统一检查注册结果，启动时列出失效项 |
| B10 | 未做 | 诊断日志需要新设置项与隐私设计（不得记录翻译原文），建议单独排期 |
| B11 | 已修 | `RestartForNewLanguage` 前把翻译窗摘出交给新窗口，并同步其配置引用，避免写回旧配置 |
| C1 | 已修 | 新增 `HttpSupport`，User-Agent 与请求头约定下沉；`TranslateHttp.UserAgent` 转发保留 |
| C2 | 保留 | `timeout /t` 在无控制台的进程里会因 stdin 缺失直接返回，起不到等待作用，故继续用 `ping` 延时并补充注释说明原因 |
| C3 | 已修 | 今日高亮色改为 `App.xaml` 的 `TodayBackground` 资源 |
| C4 | 已修 | 新增 `WidgetSettings.IsTopmost`，菜单切换后持久化 |
| C5 | 已修 | `ApplyLock()` 统一控制 `ResizeMode`，锁定后同时禁止拖动与缩放 |
| C6 | 已修 | 刷新间隔先 `NormalizeRefresh` 归一（无效旧值回落 60），下拉不再静默选中第一项 |
| C7 | 已修 | 写注册表失败改为弹窗提示，不再静默吞掉 |

### 评审之外新发现并修复的两个问题

1. **`Loc.Get` 在资源缺失时会抛 `MissingManifestResourceException`**，而不是降级显示键名。发布时若漏掉卫星程序集或中性资源，界面会直接崩。已改为捕获该异常并返回键名。
2. **`UpdateService.ParseLatestRelease` 在找到第一个 `.msi` 后立即 `break`**，导致 Release 里排在安装包之后的 `SHA256SUMS.txt` 永远取不到。已改为扫完整个 `assets`。

### 验证结果

`dotnet build` 在本机沙箱中因 NuGet 还原失败（`Value cannot be null (Parameter 'path1')`）走不通，改用 `tools/roslyn-check.sh` 直接驱动 Roslyn 编译：

| 对象 | 结果 |
| --- | --- |
| `DesktopCalendarWidget` 主工程 | 编译通过，0 错误 0 警告 |
| `ScheduleTests` | 27 项 / 417 断言 / 0 失败 |
| `TranslateTests` | 69 项 / 145 断言 / 0 失败（原 57 项，新增 12 项） |

新增的回归用例覆盖：节假日解析（timor 风格响应、调休识别、同日优先级、非法日期丢弃）、`FindChecksum` 两种格式、Release 解析同时收集校验文件、配置迁移三条路径、农历正月初一与干支六十年循环。为此把 `HolidayService.Parse`、`SettingsService.Migrate` 改为 `internal`，并把干支计算从 `GetYearLabel` 中提取为纯函数 `StemBranchOf`。

仍未验证：P/Invoke 行为（鼠标穿透、托盘、全局快捷键）、UI 实际观感、外部接口连通性。

---

## 修复记录·第二轮（2026-09-23，复审 1.5.0 全部新功能）

在第一轮修复全部落地后，对当前工作区相对 `9526210` 的全部改动（i18n、翻译、天气、自动更新、界面放大、发布流水线）再做一轮完整评审。新发现 6 条，全部已修。

| 编号 | 级别 | 状态 | 改动要点 |
| --- | --- | --- | --- |
| R1 | A | 已修 | `OpenSettings` 原来用 `settings = dialog.Settings` 直接换引用：`UpdateFlow`（构造于 `MainWindow_Loaded`）与 `TranslateWindow` 持有的是旧对象，它们之后的 `Save`（后台更新检查的 `MarkChecked`、关翻译窗、切置顶）会把旧对象整份写盘，**覆盖掉刚保存的全部设置**；「自动检查更新」开关和翻译接口选择也跟着不生效。改为新增 `WidgetSettings.CopyFrom` 逐字段拷贝、保持引用不变 |
| R2 | B | 已修 | `UpdateFlow.CheckInBackgroundAsync` 在检查**前**就 `MarkChecked()` 落盘：开机 8 秒首查常赶上网卡未就绪，一次静默失败就把当天剩余的自动检查全部推掉。改为成功拿到结果（含「已是最新」）后才记录检查时间 |
| R3 | B | 已修 | 设置窗口「立即更新天气」用编辑中未保存的城市刷新共享的 `WeatherService`；用户随后点「取消」，界面会一直显示没保存的那个城市的天气。取消路径现在回拉一次当前配置城市的天气（`StartWeather` + 强制刷新），与节假日的 `HolidaysPersisted` 同步语义对齐 |
| R4 | C | 已修 | `MainWindow` 对 `WeatherRefreshMinutes` 的 clamp 与设置窗口 `NormalizeRefresh` 口径不一：旧值为 0 时主窗口按 5 分钟刷，设置界面显示的却是 60。已提取 `NormalizedRefreshMinutes` 统一为「0/负值按 60」 |
| R5 | C | 已修 | `TranslateHttp` 自带 `UserAgent` 常量并手工拼请求头，与 `HttpSupport.ApplyUserAgent` 重复（第一轮 C1 的遗留）。已统一走 `ApplyUserAgent`，删除转发常量 |
| R6 | C | 已修 | `MyMemoryTranslateProvider` 注释「只作为降级链的最后一环在线兜底」与实际首选顺序矛盾。已按真实顺序改写 |

### R1 的完整推导

`SettingsWindow` 编辑的是 `settings.Clone()`（第一轮 A2 的修法）；保存时 `MainWindow` 用副本替换了字段引用。但 `UpdateFlow.settings` 在 `MainWindow_Loaded` 就已绑定旧实例，`TranslateWindow.settings` 在打开翻译窗时绑定。引用被替换后：

- `UpdateFlow.MarkChecked()`（每 12 小时一次 + 每次手动检查）→ `settingsService.Save(旧对象)` → `settings.json` 被旧值整体覆盖；
- `TranslateWindow` 关闭 / 切换置顶时同样写旧对象；
- `UpdateFlow.Reschedule()` 读旧对象的 `AutoCheckUpdates`，设置里关掉自动检查不生效；`TranslateWindow.RunAsync` 读旧对象的 `TranslateProvider`。

修法是保住「唯一权威实例」：`CopyFrom` 把副本字段拷回原实例，所有持有者自然读到新值。回归测试用反射枚举全部可持久化属性逐一核对（`copy from transfers every persisted field`），以后新增配置字段忘了加进 `CopyFrom` 会直接测试失败。

### 验证结果（第二轮）

本机 NuGet 还原问题已定位为进程环境缺少 `APPDATA`/`PROGRAMFILES`，补齐后 `dotnet build` 全链路可用（详见当日 memory）。本次验证：

| 对象 | 结果 |
| --- | --- |
| `DesktopCalendarWidget` 主工程（Release） | 0 错误 0 警告 |
| `TranslateTests` | 71 项 / 179 断言 / 0 失败（新增 2 项 CopyFrom 用例） |
| `ScheduleTests` | 27 项 / 417 断言 / 0 失败 |

复审过但未发现问题的点：更新下载的 `.part`→改名原子性、校验失败删除安装包、`DownloadDialog` 关闭即取消、Release 资产命名与 `UpdateService` 期望一致、流水线标签与 `<Version>` 强校验、`TranslateCache` 合并写盘的并发 dirty 保护、天气快照引用比对避免重复重绘、`RestartForNewLanguage` 的关机模式切换与翻译窗交接。

仍未验证：P/Invoke 行为（穿透、托盘、快捷键）与 R1~R3 的修复效果需要实机过一遍；外部接口连通性不在评审范围。

### 打包安装与运行时验证（同日）

- 产出 `artifacts/DesktopCalendarWidget-1.5.0-win-x64.msi` 并升级本机安装：旧产品（1.4.0）已注销，`%LocalAppData%\Programs\DesktopCalendarWidget\DesktopCalendarWidget.exe` 的 FileVersion = **1.5.0.0**；`%LocalAppData%\DesktopCalendarWidget` 下的配置、节假日、天气缓存、翻译缓存均原样保留（`StartWithWindows` 仍为 true）。
- 另建临时 WPF 宿主工程（放 `%TEMP%`，用 ProjectReference 引用生产工程）把**真实的 `App` 与两个窗口**跑了一遍，验证只在运行时才解析的东西：

  | 检查项 | 结果 |
  | --- | --- |
  | 主窗口 XAML 加载 + 布局 | 通过（640x820，StaticResource / `x:Static loc:Loc.*` / `Icon` 的 pack URI 全部解析成功） |
  | 翻译窗无标题栏 | `WindowStyle=None`，且 `ResizeMode=CanResize`（仍可缩放），实测布局 560x421 |
  | 背景天气动画 | 逐档切换 10 种天气，粒子数与设计一致：Unknown 0、Clear 1、PartlyCloudy 3、Overcast 4、Fog 3、Drizzle 12、Rain 18、HeavyRain 26、Snow 16、Thunderstorm 22 |
  | 关闭天气 / 无当天数据 | 回到静态底色，粒子清空为 0 |

  整个过程中未出现未处理异常。

- 仍未验证（需真人过一遍）：鼠标穿透、托盘图标、全局快捷键三项 P/Invoke 行为；无边框翻译窗的拖动与缩放手感；背景动画的观感与 CPU 占用。

---

## 一、总体结论

代码质量明显高于同体量的个人项目：分层清晰（`Translate/`、`Weather/`、`Update/UpdateService.cs` 不碰 WPF，可被测试工程直接链接）、URL 构造与响应解析一律做成纯函数以便离线测试、第三方依赖为零、异常边界处理普遍到位（文件读写、托盘注册失败重试、下载先写 `.part` 再改名）。排班推算（`RestSchedule.cs`）是本次评审中逻辑密度最高也最扎实的一块，配套 28 个回归用例覆盖了大小周顺延、零休周、手动基准等边界。

主要短板集中在 **进程级可靠性**（单实例互斥锁）、**配置修改的"取消"语义**、**更新通道的完整性校验**，以及 UI 线程上的若干同步 I/O。下面按优先级列出。

---

## 二、A 类：建议修复（正确性 / 安全 / 可靠性）

### A1. 进程被强杀后，下次启动可能因 AbandonedMutexException 崩溃
`App.xaml.cs:17`

```csharp
singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstance.MutexName, out var createdNew);
```

`initiallyOwned: true` 会在构造时执行 `WaitOne`。若上一个进程被任务管理器结束或崩溃退出，内核会把该互斥体标记为 abandoned，本次 `WaitOne` 会抛 `AbandonedMutexException`——这是未捕获异常，出现在启动路径上，表现为**程序起不来**。挂件类程序被强杀/随系统关机是常态，触发概率不低。

建议：改用 `initiallyOwned: false` + `WaitOne(TimeSpan.Zero)`，并显式 `catch (AbandonedMutexException)` 视为"获取成功"：

```csharp
singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstance.MutexName, out _);
bool createdNew;
try { createdNew = singleInstanceMutex.WaitOne(TimeSpan.Zero); }
catch (AbandonedMutexException) { createdNew = true; }   // 上一实例异常退出，本实例接管
```

### A2. 设置窗口的"取消"语义不干净，且与实际代码行为矛盾
`SettingsWindow.xaml.cs:184-187`

```csharp
// 仅更新内存中的地址，最终由"保存"按钮统一落盘，取消时不会改动配置。
Settings.HolidayUpdateUrl = urlTemplate;
Holidays = Holidays.Where(...).Concat(downloaded).ToList();
settingsService.SaveHolidays(Holidays);   // ← 这里已经落盘了
```

三个问题叠在一起：

1. 注释说"不落盘"，但下一行就 `SaveHolidays` 写盘了。用户点「更新节假日数据」后点**取消**，磁盘上的 `holidays.json` 已经变了，而主窗口内存里的 `holidays` 没变——界面与磁盘不一致，要下次启动才对上。
2. `Settings` 与主窗口的 `settings` 是**同一个对象引用**（`MainWindow.OpenSettings` 传的是 `settings` 本身）。写 `Settings.HolidayUpdateUrl` 等于直接改了主窗口的配置；取消也不会回滚，主窗口下次保存（例如拖动窗口后退出）就会把它带进 `settings.json`。
3. 结果是「取消」只取消了部分修改。

建议二选一：
- 简单版：让「更新节假日数据」照现在的样子即时落盘（这是合理行为），但同步刷新主窗口的数据源，并且在设置窗口构造时深拷贝一份 `WidgetSettings`（`Settings = Clone(settings)`），保存时由 `MainWindow` 整体替换——彻底断开共享引用。
- 保守版：把 `Settings.HolidayUpdateUrl` 的赋值挪到 `SaveButton_Click`，取消时所有修改一律不生效。

### A3. 自动更新的安装包下载后未做任何完整性校验
`Update/UpdateService.cs:129-160`、`Update/UpdateFlow.cs:148-174`

下载完成直接 `msiexec /i` 安装。Release 里已经提供了 `SHA256SUMS.txt`（`RELEASE_NOTES.md:27`），但程序没用。虽然全程 HTTPS 已解决了传输层风险，但仍缺少两道防线：仓库被入侵/误传附件、以及下载中断后 `File.Move` 前未被发现的截断（`.part` 改名只防了"进程中断"，防不了"连接被切断但流正常结束"）。

建议：下载后比对 GitHub 给的 `asset.size`（`UpdateInfo.InstallerBytes` 已有），并尝试下载同名 `SHA256SUMS.txt` 校验；两者都拿不到时，至少校验实际字节数是否等于 `InstallerBytes`，不符就报失败而不是静默安装。

### A4. 下载进度窗点右上角 ✕ 关闭时不会取消下载
`Update/UpdateFlow.cs:230-290`

`Cancellation.Cancel()` 只挂在「取消」按钮上。用户点窗口关闭按钮（或 Alt+F4）时 `ShowDialog()` 直接返回，外层 `await task` 会一直等到下载结束——取消形同虚设，用户只能干等几十 MB。

建议：`DownloadDialog` 重写 `OnClosing`（或在 `Closed` 事件里）调用 `Cancellation.Cancel()`，让两个关闭路径行为一致。

### A5. 两个 WeatherService 实例并发写同一个 `weather.json`
`MainWindow.xaml.cs:546` 与 `SettingsWindow.xaml.cs:54` 各 new 一个 `WeatherService`，指向同一缓存文件。

`WeatherService.LoadAsync` 用实例级的 `IsLoading` 做并发保护，跨实例无效。主窗口的定时刷新与设置窗口的「立即更新天气」可能同时 `WriteCache`（`File.WriteAllText` 非原子），轻则本次写入被吞（已 catch `IOException`），重则留下半个 JSON——下次 `ReadCache` 解析失败返回 null，表现为天气整片消失。

同时，设置窗口判断成功的条件是 `updated || current is null` → 若主窗口那次刷新抢先占用导致本实例返回 `false`，界面会**误报「更新失败」**。

建议：让 `WeatherService` 成为主窗口持有的单例并注入设置窗口；或者给缓存写操作加一个进程内 `SemaphoreSlim`，并把"成功"判定改为看 `Current` 是否被刷新而不是看 `updated` 布尔值。

---

## 三、B 类：建议改进（健壮性 / 体验）

### B1. 鼠标穿透所依赖的 `WS_EX_LAYERED` 可能被 WPF 覆盖回去 —— 需实机验证
`MainWindow.xaml.cs:581-599`

`ApplyClickThrough()` 手动 `GetWindowLongPtr/SetWindowLongPtr` 改 `GWL_EXSTYLE`，加上 `WS_EX_LAYERED | WS_EX_TRANSPARENT`。而 `MainWindow.xaml:6` 已声明 `AllowsTransparency="True"`，WPF 自己也在管理这个 extended style（透明度变化时它会重写）。若用户在设置里改过透明度，`Opacity` 变化触发 WPF 重写 style，**穿透可能被静默清掉**（表现：勾选了穿透但鼠标仍能点中挂件）。

建议：去掉手动加 `WS_EX_LAYERED` 的那一行（`AllowsTransparency=True` 已经保证 layered），只保留 `WS_EX_TRANSPARENT` 的增删；并在 `OpenSettings` 里改完 `Opacity` 后重新 `ApplyClickThrough()`。改完请在目标机验证「改透明度 → 穿透仍生效」。

### B2. 节假日下载请求没带 User-Agent
`HolidayService.cs:10-15`

`TranslateHttp`、`WeatherHttp`、`UpdateService` 都统一带了浏览器 UA，只有 `HolidayService` 的 `HttpClient` 是裸的。timor.tech 这类公开接口对无 UA 请求返回 403/非 JSON 并不罕见，一旦发生，`UpdateStatus` 会显示一条用户看不懂的异常信息。

建议：复用 `TranslateHttp.UserAgent`（顺带把 UA 常量下沉到一个公共的 `HttpSupport`，见 C1）。

### B3. 翻译缓存未命中就全量读盘反序列化，且发生在 UI 线程
`Translate/TranslateCache.cs:49-64`

`TryGet` 内存 LRU 未命中时，会 `ReadDiskEntries()` 把 500 条磁盘缓存整个读出来反序列化。翻译窗是「停止输入 400ms 即触发」的交互场景，每次新句子都会产生一次磁盘读，`RunAsync` 又在 UI 线程上 await（无 `ConfigureAwait(false)`），频繁使用时会掉帧。

建议：进程启动时一次性把磁盘索引读进内存（500 条字典，内存开销可忽略），之后只在 `FlushToDisk` 时写；或者把磁盘查询包进 `Task.Run`。

### B4. 天气缓存的读写是同步文件 I/O，同样在 UI 线程
`Weather/WeatherService.cs:158-199`

`ReadCache`/`WriteCache` 直接 `File.ReadAllText`/`WriteAllText`。单次几十 KB，量不大，但每 30~360 分钟一次、且总是紧跟一次 42 格重绘。建议改 `ReadAllTextAsync` / `WriteAllTextAsync`。

### B5. 节假日解析的同日去重规则与运行时不一致
`HolidayService.cs:53-57` 用 `GroupBy(Date).First()`；而运行时 `RestSchedule.CreateHolidayMap` 用「调休上班 > 放假 > 无标记」的优先级。

某些数据源同一天既有放假又有调休记录时，下载阶段会随机保留先出现的那条，优先级判断随后失效。建议 `Parse` 的收尾直接复用 `CreateHolidayMap` 的优先级逻辑（把 `RestSchedule` 的 `Priority` 提取为 `HolidayEntry` 的静态比较器）。

### B6. 切换城市失败时，日历仍显示旧城市的天气且无任何提示
`Weather/WeatherService.cs:43-52`：缓存城市与请求城市不同时不调用 `SetCurrent(cached)`，`Map` 保持上一次（旧城市）的内容；若随后所有数据源都失败，用户看到的是旧城市天气，却以为是新城市的。

建议：拉取失败时保留快照，但把 `Provider` 字段标记为「旧数据 / 城市不匹配」，图例行（`BuildLegendText`）显示出来。

### B7. 启动时日历渲染两次
`MainWindow.xaml.cs:96-97`：`RenderCalendar(); StartWeather();` —— 首次渲染时 `weatherService` 还是 null，`weatherMap` 为空；天气到货后 `RefreshWeatherAsync` 里再渲染一次。首屏会看到一次"无天气 → 有天气"的跳动。

建议：把 `weatherService` 的创建提到 `Loaded` 最前面（构造里就能建），并让 `RenderCalendar` 先读一次缓存；或者首屏先渲染骨架，等天气就绪再一次性渲染。

### B8. 位置只在关闭时保存，尺寸完全不保存
`MainWindow.xaml.cs:121-124`。异常退出（崩溃、被强杀、更新安装时 `Application.Shutdown`）会丢失拖动后的位置；窗口尺寸每次启动回到 640×820，与 README「可拖拽边缘临时缩放」的承诺有落差。

建议：位置在拖动结束后即保存（`Header` 的 `MouseLeftButtonUp`/`LocationChanged` 里节流写盘），并把 `Width/Height` 纳入 `WidgetSettings`。

### B9. 位置锁定快捷键注册失败没有任何提示
`MainWindow.xaml.cs:75-76`：只检查了穿透快捷键（`Ctrl+Alt+C`）的注册结果并降级，`Ctrl+Alt+L` 的返回值被丢弃。被占用时用户勾选「锁定位置」后仍可拖动，没有任何反馈。

建议：两个快捷键统一走一个 `RegisterHotkey(id, ...)` 辅助方法，收集失败项，在启动时一次性提示。

### B10. 全程静默，没有诊断通道
所有网络与文件异常都被 `catch {}` 吞掉（挂件类程序这样做有道理，不能弹窗打断用户），但排障时等于两眼一抹黑——用户反馈「天气没了」时无法定位是缓存损坏、接口失败还是城市解析失败。

建议：加一个可选的文件日志（数据目录下 `diagnostics.log`，默认关闭，设置里可开），只记录异常与关键状态，且**不记录用户输入的翻译原文**（隐私）。

### B11. 切换语言重建主窗口会连带关掉翻译窗
`MainWindow.xaml.cs:116-120` 在主窗口 `Closing` 里关翻译窗（这是必要的，否则进程不退出），但 `App.RestartForNewLanguage` 也会走同一路径。用户如果在翻译时顺手改了语言，翻译窗连同译文一起消失。

建议：`RestartForNewLanguage` 前把翻译窗摘出来（置 `Owner` 为 null 并从关闭列表里排除），重建完成后再挂回去。

---

## 四、C 类：可选优化

| # | 位置 | 说明 |
| --- | --- | --- |
| C1 | `Update/UpdateService.cs:116` | 更新模块为了拿 UA 去引用 `Translate.TranslateHttp.UserAgent`，跨模块耦合。建议下沉到公共 `HttpSupport`。 |
| C2 | `Update/UpdateFlow.cs:184` | 用 `cmd /c ping -n 4 127.0.0.1 >nul & msiexec` 做 3 秒延时是 hack（依赖 ICMP 行为）。可改用 `timeout /t 3 /nobreak`，或创建一个具名事件由安装前脚本等待。 |
| C3 | `MainWindow.xaml.cs:395` | 今日高亮背景 `Color.FromRgb(241,232,221)` 硬编码，没走 `FindBrush`。建议进 `App.xaml` 资源，与配色体系保持一致。 |
| C4 | `MainWindow.xaml.cs:486-487` | 菜单里的「置顶」只改 `Topmost`，不写配置，重启即回退。要么持久化，要么在菜单里说明是临时的。 |
| C5 | `MainWindow.xaml.cs:470-473` | `IsLocked` 只阻止拖动，不阻止边缘缩放（窗口 `ResizeMode="CanResize"`）。锁定语义建议二选一并写进提示。 |
| C6 | `SettingsWindow.xaml.cs:66-73` | 刷新间隔下拉的回填逻辑：`IndexOf` 找不到旧值时 `Math.Max(0, -1)` 会静默变成 30 分钟。旧配置里 0 或负数会被悄悄改写。建议显式校验区间。 |
| C7 | `StartupService.cs:22` | 开机启动写注册表失败被 `catch {}` 吞掉，用户勾选后无提示。建议失败时在设置界面给一行红字。 |

---

## 五、测试覆盖

现有两套回归工程（`tests/`），共约 700 行、60+ 用例，全部离线：

- `ScheduleTests`（28 例）：休息日推算的边界相当扎实——大小周顺延、零休周翻转、手动基准不被历史调休覆盖、重复日期优先级、跨年跨月、百年跨度性能。这块可以放心改。
- `TranslateTests`（约 35 例）：语种识别、离线词典切词与短语优先、两家在线接口的 URL 与响应解析、缓存淘汰与磁盘往返、降级链与熔断、地理编码解析、天气字段容错、Release 解析与版本号归一。

**缺口**（建议按此顺序补）：

1. `SettingsService` 完全没测：配置迁移（`Migrate`）、损坏 JSON 回退、`HolidayUpdateUrl` 缺 `{0}` 的兜底。这是 A2 的修复前提。
2. `HolidayService.Parse` 没测：timor.tech 真实响应格式、`holiday:false` 的调休识别、同日重复（B5 的修复前提）。
3. `LunarCalendarConverter` 没测：闰月（如 2025 闰六月）与干支年边界。
4. 单实例与 `AbandonedMutexException` 路径（A1）——难以自动化，至少手工验证一次：启动 → 任务管理器结束进程 → 再次启动。

---

## 六、建议的修复顺序

1. **A1**（启动失败）+ **A2**（配置取消语义）—— 影响日常使用，改动面小。
2. **A3**（更新包校验）+ **A4**（下载取消）—— 涉及安装行为，建议在下一个版本发布前完成。
3. **A5**（天气双实例）—— 顺手做单例化。
4. **B1、B2、B7** —— 需要实机验证，可放进下一个版本的验证清单。
5. 其余 B/C 类按迭代节奏安排，优先 B8（尺寸持久化）和 B10（诊断日志），这两项是用户最容易感知到的。

---

## 七、评审未覆盖的部分

- 未编译、未实机运行，UI 表现与 P/Invoke 行为（穿透、托盘、快捷键）均未在目标 Windows 环境验证。
- `installer/`（WiX 打包脚本）、`.github/workflows/release.yml`、`Strings.resx` / `Strings.en-US.resx` 文案内容、`Translate/Data/zh-en-dict.json` 词条质量未纳入本次评审。
- 未在联网环境下验证 timor.tech、Open-Meteo、wttr.in、MyMemory、Google 五个外部接口的当前可用性。
