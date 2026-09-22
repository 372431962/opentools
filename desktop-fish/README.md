# DesktopFish 🐟

让 Windows 桌面上的**所有图标（logo）像小鱼一样游动**的小玩具。

启动后，桌面上的快捷方式、文件夹、"此电脑"、回收站……每一个 logo 都会从它原来的位置"活过来"，
摆着尾巴在桌面上游来游去：会随机转向、靠近边缘时自动拐回屏幕中央、偶尔加速窜出去。
光标停在某条小鱼上，它会停下来等你；双击它就是**打开那个图标**，右键它弹出的是
**资源管理器给这个图标的原生菜单**。按 `ESC` 退出后，桌面图标原样恢复（**真实图标的位置从未被改动**）。

## 特性

- **零配置**：所有图标自动采集，无需准备任何图片素材。
- **图标种类全覆盖**：包括特殊的系统图标（此电脑 / 回收站 / 控制面板……），
  因为它是"截屏差分"抠出来的，而不是按文件名去找资源。
- **动画期间真实图标是隐藏的**：不会出现"原地一个 + 游动一个"的双影；
  explorer 因为刷新/新建文件把图标重新显示出来时，会立刻再隐藏一次。退出（任何方式）必定恢复。
- **桌面层渲染**：动画窗口插在桌面图标列表所在的那个根窗口（`Progman`，或开了桌面幻灯片/部分主题时
  承载 `SHELLDLL_DefView` 的 `WorkerW`）**正上方** —— 位于壁纸之上、普通窗口之下；
  打开浏览器、编辑器时小鱼乖乖待在后面，不会打扰你。
- **点击穿透**：小鱼不会挡住任何鼠标操作（窗口带 `WS_EX_TRANSPARENT`）。
- **鼠标悬停即停**：光标停在某条小鱼身上，它就停在原地继续摆尾；移开接着游。
- **双击小鱼 = 打开那个图标**：右键小鱼 = 弹出该图标的**资源管理器原生菜单**
  （重命名 / 属性 / 删除…都在）。右键是转交给 explorer 弹的；双击则按图标显示名反查
  真实路径再打开（explorer 不吃伪造的双击消息，详见「工作原理」第 8 条），
  所以快捷方式、文件夹、回收站、此电脑都有效。
- **多条小鱼各自独立**：每一帧独立摆尾、独立巡游，看起来像一群小鱼。
- **退出必定恢复**：`ESC` / 关闭控制台窗口 / `Ctrl+C` / 进程异常，都会把桌面图标恢复显示；
  万一真的遇到意外，还能双击 `restore_icons.vbs`，或运行 `dist\DesktopFish.exe --restore`（源码方式：`dotnet run --project DesktopFish.csproj --restore`）补救。

## 环境要求

- Windows 10 / 11（需要 `explorer.exe` 正在运行、桌面图标已开启）
- 运行 `dist\DesktopFish.exe`：**无需任何运行时**
- 若从源码运行 / 重新打包：需要 .NET 8.0 SDK 或更高版本

## 快速开始

**方式一：双击 `start_fish.bat`（推荐，日常用）** —— 无控制台窗口后台运行；
按 `ESC` 退出。

**方式二：双击 `run.bat`（首次排查用）** —— 保留控制台，能看到完整日志与报错。

**方式三：双击 `start_fish.vbs`（完全静默）** —— 连控制台闪一下都没有。

**方式四：手动执行**

```bat
cd desktop-fish
dotnet run --project DesktopFish.csproj --probe   :: 可选：先自检能否正确识别桌面图标
dotnet run --project DesktopFish.csproj
```

**想要一个桌面快捷方式**：双击一次 `create_desktop_shortcut.vbs`，桌面上就会出现「桌面小鱼」图标，
以后一键开玩。

按 `ESC` 退出，图标原样恢复。

## 命令行参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--speed` | `1.0` | 游动速度倍率，`0.5` 更悠闲，`2` 更疯狂 |
| `--icon-size` | `48` | 图标边长的兜底估算（96 DPI 下的像素） |
| `--margin` | `4` | 采集图标时四周外扩的像素 |
| `--threshold` | `14` | 抠像差分阈值，**越小越敏感**。低对比度图标抠不全时调小；噪点/彩色毛边过多时调大 |
| `--angles` | `48` | 每个图标预渲染的旋转角度数，越小越省内存 |
| `--fps` | `60` | 帧率上限 |
| `--top` | 关 | 顶层显示：小鱼盖在所有窗口之上（默认是置于桌面之上、窗口之下） |
| `--no-mouse` | 关 | 关闭鼠标交互（不装鼠标钩子）：悬停不停、双击/右键都还给桌面 |
| `--no-attach` | 关 | 已废弃，仅为兼容保留（现在默认就是置于桌面之上） |
| `--probe` | 关 | 自检模式：只检测并打印图标信息 |
| `--restore` | 关 | 只恢复桌面图标显示后退出 |
| `--debug` | 关 | 打印采集过程的调试信息（区域、每个图标的蒙版像素数） |
| `--opaque` | 关 | 排查用：关闭透明，用纯色背景显示窗口（确认窗口是否可见） |

## 工作原理

1. **定位图标层**：枚举顶层窗口找到 `SHELLDLL_DefView` → `SysListView32`（桌面图标列表），
   它就是"真实图标"所在的那个控件。（不再发 `WM_SPAWN_WORKERW`：那会造出遮住我们的壁纸层 `WorkerW`。）
2. **读取图标范围**：在 `explorer.exe` 进程里 `VirtualAllocEx` 一小块内存，
   用 `LVM_GETITEMCOUNT` / `LVM_GETITEMPOSITION` 拿数量与位置，
   再用 `LVM_GETITEMRECT + LVIR_ICON` 拿到图标本身的精确矩形。
   每个图标同时记住它在列表里的**序号**和**客户区坐标** —— 这是后面把鼠标动作转交给 explorer 的钥匙。
3. **抠出图标位图**：用 `PrintWindow` 让桌面图标列表控件把自身渲染到位图
   （未覆盖像素为纯黑），直接提取每个图标的透明蒙版。该方式**不受其它窗口遮挡影响**，
   也不需要隐藏/显示桌面图标，因此不会闪烁。
4. **预渲染旋转帧**：每个图标按 `--angles` 生成一批旋转帧，alpha 二值化后把透明区域填成
   抠像色（品红），避免分层窗口 `colorkey` 混合出彩色毛边。
5. **桌面层透明窗口**：WinForms 建一个无边框全屏窗口，用 `TransparencyKey`（品红抠像）
   做透明，加上 `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，
   再用 `SetWindowPos` 把它插到桌面图标列表所在根窗口（`Progman` 或 `WorkerW`，从列表句柄
   `GetAncestor(GA_ROOT)` 反推）正上方；
   同时 `ShowWindow(SW_HIDE)` 藏起真实图标，并且每秒补一次隐藏（explorer 刷新时会把图标抖出来）。
6. **游动行为**：每条小鱼有独立的巡游速度、摆尾频率、摆动幅度与相位；
   航向由缓慢随机游走（wander）驱动，靠近边缘时朝屏幕中心转向，
   偶尔"冲刺"；绘制时把正弦摆动叠加到航向上，并让身体沿航向法线左右摆，形成 S 形游泳轨迹。
7. **鼠标交互（窗口依旧点击穿透）**：窗口穿透就收不到鼠标消息，所以改用一个
   `WH_MOUSE_LL` **低级鼠标钩子**只"观察"不拦截。每帧用 `GetCursorPos` 判定悬停
   （小鱼会自己游进静止的光标，所以必须每帧重算，不能只靠移动事件）；
   按钮事件里若命中某条小鱼对应的图标，就把这次点击**吞掉**（`return 1`），
   免得桌面同时选中图标或弹出"桌面空白处"的菜单。钩子回调只做坐标判定，
   真正的动作通过 `BeginInvoke` 延后到 UI 队列执行——低级钩子跑多久，全系统的鼠标就卡多久。
8. **右键转交给 explorer，双击自己打开**：
   - 右键 → `LVM_SETITEMSTATE` 先选中那个图标，再向图标列表 `PostMessage` 一次
     `WM_CONTEXTMENU` + 图标的屏幕坐标，菜单由 explorer 弹，内容就是该图标的原生菜单。
     因此菜单出现在**图标的原位置**（小鱼此刻在别处游）。实测有效。
   - 双击 → **不能**用 `PostMessage` 转交：实测向 explorer 发 `WM_LBUTTONDBLCLK`
     （以及 down/up/dbl 组合、先发 MOVE 再双击、改发 `WM_ACTIVATE`、发 Enter 键）
     都只会选中图标，不会激活它——explorer 只接受真实输入路径上的激活。
     所以双击走"名字 → 路径 → 打开"：用 `LVM_GETITEMTEXTW`（跨进程读写 explorer
     内存）读回图标显示名，再用 `Shell.Application` 枚举桌面项拿到真实路径
     （`ParseName` 对虚拟项返回空，只能按名字找；.NET 8 上 `Type.InvokeMember`
     取 `Folder.Items` 会抛 `DISP_E_MEMBERNOTFOUND`，只有 `dynamic` 走得通），最后按类型执行：
     `::{GUID}` 虚拟项（此电脑/回收站）用 `explorer.exe shell:::{GUID}`；
     目录用 `explorer.exe <路径>`；快捷方式和普通文件用 `ShellExecuteW` 默认动词，保留文件关联。
   只有"此刻确实露着桌面"的点击才会转交（`WindowFromPoint` + `GetAncestor(GA_ROOT)`
   判定，根窗口从图标列表句柄反推——图标可能挂在 `Progman` 下，也可能挂在承载
   `SHELLDLL_DefView` 的 `WorkerW` 下），所以隔着窗口点向小鱼不会误打开图标。
9. **退出清理**：`FormClosing` → 卸载钩子 → `ShowWindow(SW_SHOW)` 恢复图标列表，
   `AppDomain.ProcessExit` + `Console.CancelKeyPress` 双保险。

## 文件说明

```
desktop-fish/
├─ desktop_fish.cs             # 主程序（C# 单文件）
├─ DesktopFish.csproj          # C# 项目文件（依赖 System.Drawing.Common）
├─ publish.bat                 # 构建并打包为单文件 dist\DesktopFish.exe
├─ start_fish.bat              # 一键启动（优先 dist\DesktopFish.exe）★
├─ run.bat                     # 一键启动（带控制台日志，排查用）
├─ start_fish.vbs              # 完全静默启动（需 VBScript）
├─ restore_icons.vbs           # 图标没恢复？双击这个补救
├─ create_desktop_shortcut.vbs # 生成桌面快捷方式
├─ _install_sdk.bat            # 下载并安装 .NET 8.0 SDK
├─ _find_dotnet.bat            # dotnet SDK 查找辅助
└─ README.md                   # 本文档
```

## 常见问题

- **看不到小鱼**：默认小鱼位于"壁纸之上、普通窗口之下"，如果桌面被最大化窗口完全盖住就看不到；
  按 `Win+D` 显示桌面，或改用 `--top`（顶层显示，盖在所有窗口之上，仍然点击穿透）。
- **窗口可见但没内容**：用 `--opaque` 排查——会关闭透明用纯色背景显示，能直接看出窗口是否被创建/定位。
- **控制台中文乱码**：启动脚本已执行 `chcp 65001`；若直接运行 `dist\DesktopFish.exe`，用 Windows Terminal 效果最好。
- **双击/右键没反应**：多半是那条小鱼此刻被某个窗口盖住了（判定要求点击确实落在露着的桌面上，
  否则隔着窗口点向小鱼就会误打开图标）。按 `Win+D` 露出桌面再点，或用 `--top` 让小鱼浮在最上层。
- **右键菜单弹在别处**：菜单是 explorer 弹的，它按"图标自己的坐标"决定给哪个图标出菜单，
  所以菜单位置是小鱼的**原位置**，而不是小鱼此刻所在的地方。
- **想临时交出鼠标**：`--no-mouse` 启动即可，不装钩子，桌面行为完全还原。
- **退出后图标没回来**：双击 `restore_icons.vbs`，或运行 `dist\DesktopFish.exe --restore`。

## 免责声明

本工具通过 Win32 消息隐藏桌面图标列表控件并叠加透明窗口来做效果，
并用一个 `WH_MOUSE_LL` 低级鼠标钩子观察鼠标（只在点中小鱼时吞掉那一次点击），
**不修改任何真实图标位置或文件**，退出时会恢复图标显示并卸载钩子。
但这类"操作资源管理器窗口"的技巧在不同 Windows 版本上表现略有差异，
请自行评估后使用；如果异常退出导致图标未恢复，用 `--restore` 或重启 `explorer.exe` 即可复原。
