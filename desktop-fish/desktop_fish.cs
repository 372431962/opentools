// DesktopFish —— 让 Windows 桌面上的图标（logo）像小鱼一样自由游动。
// C# 实现，使用 WinForms + P/Invoke。
//
// 编译: dotnet build DesktopFish.csproj
// 运行: dotnet run [--probe] [--restore] [--speed 0.7] [--fps 30] [--no-mouse]
//
// 动画期间真实图标被隐藏；光标悬停在小鱼上它会停在原地，双击/右键会把动作转交给 explorer。
// 仅支持 Windows。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// --------------------------------------------------------------------------- //
// 程序入口
// --------------------------------------------------------------------------- //

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        float speed = 1.0f;
        int iconSize = Config.DEFAULT_ICON_SIZE;
        int margin = Config.DEFAULT_MARGIN;
        int threshold = Config.DEFAULT_THRESHOLD;
        int angles = Config.DEFAULT_ANGLES;
        int fps = Config.DEFAULT_FPS;
        bool probe = false;
        bool restore = false;
        bool top = false;
        bool opaque = false;
        bool mouse = true;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--speed": speed = ParseFloat(args, ref i, "--speed"); break;
                    case "--icon-size": iconSize = ParseInt(args, ref i, "--icon-size"); break;
                    case "--margin": margin = ParseInt(args, ref i, "--margin"); break;
                    case "--threshold": threshold = ParseInt(args, ref i, "--threshold"); break;
                    case "--angles": angles = ParseInt(args, ref i, "--angles"); break;
                    case "--fps": fps = ParseInt(args, ref i, "--fps"); break;
                    case "--no-attach": break; // 兼容旧参数：现在默认就是置于桌面之上
                    case "--no-mouse": mouse = false; break;
                    case "--probe": probe = true; break;
                    case "--restore": restore = true; break;
                    case "--top": top = true; break;
                    case "--opaque": opaque = true; break;
                    case "--debug": IconExtractor.Debug = true; break;
                    default:
                        Console.WriteLine($"[错误] 未知参数：{args[i]}");
                        return 1;
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
            return 1;
        }

        if (!(speed > 0.05f && speed < 20f)) { Console.WriteLine("[错误] --speed 需在 0.05 ~ 20 之间"); return 1; }
        if (iconSize < 8 || iconSize > 512) { Console.WriteLine("[错误] --icon-size 需在 8 ~ 512 之间"); return 1; }
        if (margin < 0 || margin > 128) { Console.WriteLine("[错误] --margin 需在 0 ~ 128 之间"); return 1; }
        if (threshold < 0 || threshold > 255) { Console.WriteLine("[错误] --threshold 需在 0 ~ 255 之间"); return 1; }
        if (angles < 8 || angles > 360) { Console.WriteLine("[错误] --angles 需在 8 ~ 360 之间"); return 1; }
        if (fps < 15 || fps > 240) { Console.WriteLine("[错误] --fps 需在 15 ~ 240 之间"); return 1; }

        try { Win32.SetProcessDpiAwareness(Win32.DPI_AWARENESS_PER_MONITOR); } catch { }

        try
        {
            var icons = new DesktopIcons();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => icons.Show();
            Console.CancelKeyPress += (s, e) => { icons.Show(); };

            if (restore)
            {
                icons.Show(); icons.Close();
                Console.WriteLine("[DesktopFish] 已恢复桌面图标显示。");
                return 0;
            }

            float dpiScale = GetDpiScale();
            int iconPx = Math.Max(16, (int)Math.Round(iconSize * dpiScale));
            margin = Math.Max(0, (int)Math.Round(margin * dpiScale));

            if (probe)
            {
                var layout = icons.Layout(iconPx);
                Console.WriteLine($"[DesktopFish] 自检：检测到 {layout.Count} 个桌面图标（当前显示状态：{(icons.Visible ? "可见" : "隐藏")}）");
                for (int i = 0; i < Math.Min(layout.Count, 10); i++)
                {
                    var (item, center, rect, _) = layout[i];
                    Console.WriteLine($"   #{item,-3} 「{icons.ItemName(item) ?? "?"}」 中心 ({center.X},{center.Y})，图标范围 {rect.Width}x{rect.Height}，左上角 ({rect.X}, {rect.Y})");
                }
                if (layout.Count > 10)
                    Console.WriteLine($"   ... 其余 {layout.Count - 10} 个已省略");
                icons.Close();
                return 0;
            }

            Console.WriteLine($"[DesktopFish] 桌面图标 {icons.Count()} 个；DPI {dpiScale:.2f}，图标兜底 {iconPx}px");
            Console.WriteLine("[DesktopFish] 正在采集图标位图（后台渲染桌面图标层，不影响你当前的窗口）...");

            var sprites = IconExtractor.ExtractIcons(icons, iconPx, margin, threshold, angles);
            if (sprites.Count == 0)
            {
                Console.WriteLine("[错误] 没有采集到任何图标（可尝试减小 --threshold），已退出。");
                icons.Show();
                icons.Close();
                return 3;
            }

            // 使用整个虚拟桌面（支持多显示器 / 负坐标副屏）
            int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
            int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
            int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
            int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0)
            {
                vx = 0; vy = 0;
                vw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
                vh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            }
            var bounds = new Rectangle(vx, vy, vw, vh);

            var rng = new Random();
            float angleStep = 360.0f / Math.Max(8, angles);
            var fishList = new List<Fish>();
            foreach (var (item, center, homeClient, hitRadius, frames) in sprites)
            {
                Point start = new Point(center.X, center.Y);
                fishList.Add(new Fish(frames, start, bounds, rng, angleStep, speed, item, homeClient, hitRadius));
            }

            // 动画期间隐藏真实图标，退出时（含异常）统一恢复
            icons.Hide();
            if (IconExtractor.Debug) Console.WriteLine($"[DBG] after Hide: icons.Visible={icons.Visible} realVisible={Win32.IsWindowVisible(icons.Hwnd)}");
            try
            {
                string layer = top ? "顶层" : "置于桌面之上（壁纸之上、窗口之下）";
                var overlay = new OverlayWindow(bounds, fps, top, opaque, fishList, icons, mouse, () =>
                {
                    icons.Show();
                    Console.WriteLine("[DesktopFish] 桌面图标已恢复，再见！");
                });

                Console.WriteLine($"[DesktopFish] {fishList.Count} 条小鱼开始游动，窗口{layer}。按 ESC 退出。");
                overlay.Run();
                overlay.Dispose();
            }
            finally
            {
                icons.Show();
                icons.Close();
            }
            return 0;
        }
        catch (RuntimeError ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 运行失败：{ex.Message}");
            return 1;
        }
    }

    static float GetDpiScale()
    {
        try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1.0f, g.DpiX / 96.0f); }
        catch { return 1.0f; }
    }

    static string RequireValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{name} 缺少参数值");
        return args[++i];
    }

    static int ParseInt(string[] args, ref int i, string name)
    {
        string v = RequireValue(args, ref i, name);
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
            throw new ArgumentException($"{name} 需要整数，收到 \"{v}\"");
        return r;
    }

    static float ParseFloat(string[] args, ref int i, string name)
    {
        string v = RequireValue(args, ref i, name);
        if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
            throw new ArgumentException($"{name} 需要数字，收到 \"{v}\"");
        return r;
    }
}

// --------------------------------------------------------------------------- //
// 配置常量
// --------------------------------------------------------------------------- //

internal static class Config
{
    public const int CHROMA_R = 255;
    public const int CHROMA_G = 0;
    public const int CHROMA_B = 254;
    public const int DEFAULT_ICON_SIZE = 48;
    public const int DEFAULT_MARGIN = 4;
    public const int DEFAULT_THRESHOLD = 14;
    public const int DEFAULT_ANGLES = 48;
    public const int DEFAULT_FPS = 60;
}

// --------------------------------------------------------------------------- //
// Win32 P/Invoke
// --------------------------------------------------------------------------- //

internal static class Win32
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr pdwResult);

    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, IntPtr dwSize, out UIntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, IntPtr dwSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int dpiAwareness);

    // verb=null 表示"默认动词"，等价于资源管理器里双击
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string? verb, string file, string? args, string? dir, int show);

    // 常量
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;
    public const int SW_BOTTOM = 15;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_EX_LAYERED = 0x00800000;
    public const uint WS_EX_TRANSPARENT = 0x00200000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint HWND_TOP = 0;
    public const uint HWND_BOTTOM = 1;
    public const uint LWA_COLORKEY = 0x00000001;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const uint WM_SPAWN_WORKERW = 0x052C;
    public const int VK_ESCAPE = 0x1B;
    public const uint LVM_FIRST = 0x1000;
    public const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    public const uint LVM_GETITEMRECT = LVM_FIRST + 14;
    public const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;
    public const uint LVM_HITTEST = LVM_FIRST + 18;
    public const uint LVM_SETITEMSTATE = LVM_FIRST + 43;
    public const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;
    public const int LVIF_TEXT = 0x0001;
    public const int LVIR_ICON = 1;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const int GA_ROOT = 2;
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;
    public const int SM_CXDOUBLECLK = 36;
    public const int SM_CYDOUBLECLK = 37;
    public const uint SMTO_NORMAL = 0x0000;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;
    public const int DPI_AWARENESS_PER_MONITOR = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // MAKELPARAM(x, y)：低 16 位 x，高 16 位 y（保留符号，支持负坐标副屏）
    public static IntPtr LParam(POINT p) => new IntPtr((p.X & 0xFFFF) | ((p.Y & 0xFFFF) << 16));
}

// --------------------------------------------------------------------------- //
// 桌面窗口定位
// --------------------------------------------------------------------------- //

internal static class DesktopWin32
{
    public static IntPtr FindDesktopListView()
    {
        IntPtr progman = Win32.FindWindow("Progman", "Program Manager");
        // 不再发送 WM_SPAWN_WORKERW：它会创建一个覆盖在 Progman 之上的 WorkerW，
        // 使我们的叠加窗口被壁纸完全遮住。桌面图标列表在 Progman 的 SHELLDLL_DefView 下即可找到。
        var shells = new List<IntPtr>();
        Win32.EnumWindows((hwnd, lParam) =>
        {
            IntPtr child = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero) shells.Add(child);
            return true;
        }, IntPtr.Zero);
        if (shells.Count == 0 && progman != IntPtr.Zero)
        {
            IntPtr child = Win32.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero) shells.Add(child);
        }
        foreach (var shell in shells)
        {
            IntPtr lv = Win32.FindWindowEx(shell, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero) return lv;
        }
        return IntPtr.Zero;
    }
}

// --------------------------------------------------------------------------- //
// 双击的落地：图标列表只给得到显示名，靠 Shell.Application 枚举回真实路径，再按项的类型
// 交给 explorer.exe 或 ShellExecute（见 Open 里的分支）。
// --------------------------------------------------------------------------- //

internal static class ShellLaunch
{
    private static readonly object? _shellApp =
        Type.GetTypeFromProgID("Shell.Application") is Type t ? Activator.CreateInstance(t) : null;

    // ParseName 对虚拟项返回空，所以只能按名字在桌面项里找。
    // .NET 8 上 Type.InvokeMember 调 Folder.Items 会抛 DISP_E_MEMBERNOTFOUND，只有 dynamic 的
    // 后期绑定走通，因此这里用 dynamic 而不是反射。
    public static string? DesktopItemPath(string displayName)
    {
        if (_shellApp == null) return null;
        try
        {
            dynamic app = _shellApp;
            dynamic items = app.NameSpace(0).Items();
            for (int i = 0, n = (int)items.Count; i < n; i++)
            {
                dynamic item = items.Item(i);
                if ((string)item.Name != displayName) continue;
                return (string)item.Path;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopFish] 枚举桌面项失败：{ex.Message}");
        }
        return null;
    }

    public static bool Open(string path)
    {
        // ::{GUID} 这种写法 ShellExecute 认不出来（返回成功但什么都不开），explorer.exe 的 shell::: 形式才行
        if (path.StartsWith("::", StringComparison.Ordinal)) return ViaExplorer("shell" + path);
        // 目录交给 explorer.exe，等价于系统自带的 Folder\shell\open 命令
        // "%SystemRoot%\explorer.exe %1"。本机这条被第三方文件管理器的应用别名占用，
        // ShellExecute 会静默失败（返回值仍表示成功），所以不走默认动词。
        if (System.IO.Directory.Exists(path)) return ViaExplorer(path);
        // 快捷方式和普通文件仍用默认动词，保持用户的文件关联
        return Win32.ShellExecuteW(IntPtr.Zero, null, path, null, null, Win32.SW_SHOWNORMAL).ToInt64() > 32;
    }

    private static bool ViaExplorer(string arg)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(arg);   // ArgumentList 负责加引号，路径带空格也不会被拆开
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopFish] 启动 explorer 失败：{ex.Message}");
            return false;
        }
    }
}

// --------------------------------------------------------------------------- //
// 桌面图标控制
// --------------------------------------------------------------------------- //

internal class DesktopIcons : IDisposable
{
    private const int RemoteSize = 1024;
    private const int TextOffset = 64;          // 远程缓冲里放文字的位置
    private const int TextChars = 128;          // 图标名最多读这么多字符
    public IntPtr Hwnd { get; private set; }
    public IntPtr Process { get; private set; }
    public IntPtr Buffer { get; private set; }
    public bool Visible { get; private set; }
    // 图标列表客户区原点的屏幕坐标（屏幕坐标 - 它 = 图标列表客户区坐标）
    public Win32.POINT ScreenOrigin { get; private set; }
    private bool _closed = false;

    public DesktopIcons()
    {
        Hwnd = DesktopWin32.FindDesktopListView();
        if (Hwnd == IntPtr.Zero)
            throw new RuntimeError("未找到桌面图标列表 (SysListView32)。");
        Win32.GetWindowThreadProcessId(Hwnd, out int pid);
        Process = Win32.OpenProcess(Win32.PROCESS_VM_OPERATION | Win32.PROCESS_VM_READ | Win32.PROCESS_VM_WRITE, false, pid);
        if (Process == IntPtr.Zero)
            throw new RuntimeError("无法打开 explorer.exe 进程。");
        // 前 64 字节放 LVITEM/LVHITTESTINFO 这类小结构，其后留给图标文字（LVM_GETITEMTEXT 要两级指针）
        Buffer = Win32.VirtualAllocEx(Process, IntPtr.Zero, RemoteSize, Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_READWRITE);
        if (Buffer == IntPtr.Zero)
            throw new RuntimeError("无法在 explorer.exe 中申请共享内存。");
        Visible = Win32.IsWindowVisible(Hwnd);
        Win32.POINT origin = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(Hwnd, ref origin);
        ScreenOrigin = origin;
    }

    public int Count() => Win32.SendMessage(Hwnd, Win32.LVM_GETITEMCOUNT, 0, 0);

    // 向 explorer.exe 的远程缓冲区写入数据（不能用 Marshal.Copy，那是本进程内存）
    private bool WriteRemote(byte[] data, int offset = 0)
    {
        IntPtr local = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, local, data.Length);
            return Win32.WriteProcessMemory(Process, Buffer + offset, local, new IntPtr(data.Length), out UIntPtr written)
                   && (int)written == data.Length;
        }
        finally { Marshal.FreeHGlobal(local); }
    }

    // 从 explorer.exe 的远程缓冲区读取数据
    private bool ReadRemote(byte[] data, int offset = 0)
    {
        IntPtr local = Marshal.AllocHGlobal(data.Length);
        try
        {
            if (!Win32.ReadProcessMemory(Process, Buffer + offset, local, new IntPtr(data.Length), out UIntPtr read) || (int)read < data.Length)
                return false;
            Marshal.Copy(local, data, 0, data.Length);
            return true;
        }
        finally { Marshal.FreeHGlobal(local); }
    }

    private Win32.POINT ItemClientPos(int index)
    {
        Win32.SendMessage(Hwnd, Win32.LVM_GETITEMPOSITION, index, Buffer);
        byte[] data = new byte[8];
        if (!ReadRemote(data))
            throw new RuntimeError("读取图标位置失败 (ReadProcessMemory)。");
        return new Win32.POINT { X = BitConverter.ToInt32(data, 0), Y = BitConverter.ToInt32(data, 4) };
    }

    // 图标矩形（列表控件客户区坐标，未转屏幕坐标）；拿不到返回 null
    private Win32.RECT? ItemIconRectClient(int index, int limit)
    {
        byte[] v = BitConverter.GetBytes(Win32.LVIR_ICON);
        if (!WriteRemote(v)) return null;
        Win32.SendMessage(Hwnd, Win32.LVM_GETITEMRECT, index, Buffer);
        byte[] raw = new byte[16];
        if (!ReadRemote(raw)) return null;
        int left = BitConverter.ToInt32(raw, 0);
        int top = BitConverter.ToInt32(raw, 4);
        int right = BitConverter.ToInt32(raw, 8);
        int bottom = BitConverter.ToInt32(raw, 12);
        int w = right - left, h = bottom - top;
        if (w < 6 || h < 6 || w > limit || h > limit) return null;
        return new Win32.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    // Item = SysListView32 里的真实序号（隐藏后仍可用它操作 explorer）
    public List<(int Item, Point Center, Rectangle Rect, Win32.POINT ClientCenter)> Layout(int fallbackSize)
    {
        int limit = Math.Max(16, fallbackSize) * 4;
        var result = new List<(int, Point, Rectangle, Win32.POINT)>();
        for (int i = 0; i < Count(); i++)
        {
            try
            {
                Win32.POINT pos = ItemClientPos(i);
                Win32.RECT rc = ItemIconRectClient(i, limit)
                    ?? new Win32.RECT { Left = pos.X, Top = pos.Y, Right = pos.X + fallbackSize, Bottom = pos.Y + fallbackSize };
                Win32.POINT p1 = new Win32.POINT { X = rc.Left, Y = rc.Top };
                Win32.POINT p2 = new Win32.POINT { X = rc.Right, Y = rc.Bottom };
                Win32.ClientToScreen(Hwnd, ref p1);
                Win32.ClientToScreen(Hwnd, ref p2);
                var center = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
                var rect = new Rectangle(p1.X, p1.Y, p2.X - p1.X, p2.Y - p1.Y);
                var client = new Win32.POINT { X = (rc.Left + rc.Right) / 2, Y = (rc.Top + rc.Bottom) / 2 };
                result.Add((i, center, rect, client));
            }
            catch { continue; }
        }
        return result;
    }

    // 客户区坐标落在哪个图标上（-1 表示桌面空白处）；图标虽被隐藏，几何信息仍在
    public int HitTest(Win32.POINT clientPt)
    {
        byte[] v = new byte[16];
        BitConverter.GetBytes(clientPt.X).CopyTo(v, 0);
        BitConverter.GetBytes(clientPt.Y).CopyTo(v, 4);
        if (!WriteRemote(v)) return -1;
        Win32.SendMessage(Hwnd, Win32.LVM_HITTEST, 0, Buffer);
        byte[] raw = new byte[16];
        return ReadRemote(raw) ? BitConverter.ToInt32(raw, 12) : -1;
    }

    // 屏幕坐标 → 图标列表客户区坐标 → 命中测试（返回该处的图标序号，-1 表示空白处）
    public int HitTestScreen(Win32.POINT screenPt)
        => HitTest(new Win32.POINT { X = screenPt.X - ScreenOrigin.X, Y = screenPt.Y - ScreenOrigin.Y });

    // 只选中目标图标（lParam = NULL 表示"不带状态掩码，直接设为已选中"），
    // 让 explorer 的原生菜单与默认动作作用在它身上
    public void SelectItem(int index)
    {
        var none = new IntPtr(0);
        Win32.SendMessage(Hwnd, Win32.LVM_SETITEMSTATE, -1, none);
        Win32.SendMessage(Hwnd, Win32.LVM_SETITEMSTATE, index, none);
    }

    // 图标当前的客户区中心（动画期间一般不会变，但重读一次更保险）
    public Win32.POINT LiveIconCenter(int index, Win32.POINT fallback)
    {
        try
        {
            Win32.RECT? rc = ItemIconRectClient(index, 4096);
            if (rc.HasValue)
                return new Win32.POINT { X = (rc.Value.Left + rc.Value.Right) / 2, Y = (rc.Value.Top + rc.Value.Bottom) / 2 };
        }
        catch { }
        return fallback;
    }

    // 图标的显示名（"此电脑""Google Chrome"这种，不含扩展名）。shell 命名空间按它找回真实路径。
    public string? ItemName(int index)
    {
        // LVITEMW(x64): mask@0 iItem@4 iSubItem@8 state@12 stateMask@16 pszText@24 cchTextMax@32
        byte[] v = new byte[48];
        BitConverter.GetBytes((uint)Win32.LVIF_TEXT).CopyTo(v, 0);
        BitConverter.GetBytes(index).CopyTo(v, 4);
        BitConverter.GetBytes((long)(Buffer + TextOffset).ToInt64()).CopyTo(v, 24);
        BitConverter.GetBytes(TextChars).CopyTo(v, 32);
        if (!WriteRemote(v)) return null;
        Win32.SendMessage(Hwnd, Win32.LVM_GETITEMTEXTW, index, Buffer);
        byte[] raw = new byte[TextChars * 2];
        if (!ReadRemote(raw, TextOffset)) return null;
        char[] chars = new char[TextChars];
        int len = 0;
        for (int i = 0; i + 1 < raw.Length; i += 2)
        {
            char c = (char)(raw[i] | (raw[i + 1] << 8));
            if (c == '\0') break;
            chars[len++] = c;
        }
        return len == 0 ? null : new string(chars, 0, len);
    }

    // 把右键交给 explorer：lParam 用屏幕坐标，explorer 按该坐标命中图标，
    // 所以菜单弹在图标的"原位置"（小鱼现在的位置）而不是鼠标处，但弹的是这个图标的原生菜单
    public void PostContextMenu(int index, Win32.POINT fallbackClient)
    {
        Win32.POINT pt = LiveIconCenter(index, fallbackClient);
        Win32.ClientToScreen(Hwnd, ref pt);
        Win32.PostMessage(Hwnd, Win32.WM_CONTEXTMENU, Hwnd, Win32.LParam(pt));
    }

    public void Hide(float settle = 0.06f)
    {
        if (Visible) { Win32.ShowWindow(Hwnd, Win32.SW_HIDE); Visible = false; if (settle > 0) Thread.Sleep((int)(settle * 1000)); }
    }

    // explorer 刷新桌面（新建文件、按 F5、切换主题）时可能把图标列表重新显示出来，补一次隐藏
    public void EnsureHidden()
    {
        if (Visible || !Win32.IsWindow(Hwnd) || !Win32.IsWindowVisible(Hwnd)) return;
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
        if (IconExtractor.Debug) Console.WriteLine("[DBG] 桌面图标被重新显示，已再次隐藏");
    }

    public void Show(float settle = 0.06f)
    {
        if (!Visible && Win32.IsWindow(Hwnd))
        {
            Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
            Visible = true;
            if (settle > 0) Thread.Sleep((int)(settle * 1000));
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { if (Buffer != IntPtr.Zero) Win32.VirtualFreeEx(Process, Buffer, 0, Win32.MEM_RELEASE); } catch { }
        try { if (Process != IntPtr.Zero) Win32.CloseHandle(Process); } catch { }
    }

    public void Dispose() { Show(); Close(); }
}

// --------------------------------------------------------------------------- //
// 图标抠像
// --------------------------------------------------------------------------- //

internal static class IconExtractor
{
    public static bool Debug = false;

    static int CountTrue(bool[,] mask, int w, int h)
    {
        int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y, x]) n++;
        return n;
    }

    static void Dilate(bool[,] mask, int w, int h)
    {
        bool[,] outM = (bool[,])mask.Clone();
        for (int y = 1; y < h; y++)
            for (int x = 0; x < w; x++)
                outM[y, x] |= mask[y - 1, x];
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w; x++)
                outM[y, x] |= mask[y + 1, x];
        for (int y = 0; y < h; y++)
            for (int x = 1; x < w; x++)
                outM[y, x] |= mask[y, x - 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w - 1; x++)
                outM[y, x] |= mask[y, x + 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask[y, x] = outM[y, x];
    }

    static bool[,] CutoutVsBlack(Bitmap fg, int threshold, int w, int h)
    {
        // PrintWindow 渲染桌面图标层时，未被图标覆盖的像素为纯黑，故“非黑”即图标
        var mask = new bool[h, w];
        unsafe
        {
            var fd = fg.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte* fp = (byte*)fd.Scan0;
            int fs = fd.Stride;
            for (int y = 0; y < h; y++)
            {
                byte* fr = fp + y * fs;
                for (int x = 0; x < w; x++)
                {
                    int diff = Math.Max(fr[x * 4 + 2], Math.Max(fr[x * 4 + 1], fr[x * 4]));
                    mask[y, x] = diff > threshold;
                }
            }
            fg.UnlockBits(fd);
        }
        Dilate(mask, w, h);
        return mask;
    }

    static Bitmap SpriteFromArrays(Bitmap rgb, bool[,] mask, int w, int h)
    {
        var sprite = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var sd = sprite.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* sp = (byte*)sd.Scan0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (mask[y, x])
                    {
                        Color c = rgb.GetPixel(x, y);
                        sp[idx * 4] = c.B; sp[idx * 4 + 1] = c.G; sp[idx * 4 + 2] = c.R; sp[idx * 4 + 3] = 255;
                    }
                    else
                    {
                        sp[idx * 4] = 0; sp[idx * 4 + 1] = 0; sp[idx * 4 + 2] = 0; sp[idx * 4 + 3] = 0;
                    }
                }
        }
        sprite.UnlockBits(sd);
        return sprite;
    }

    static Bitmap FlattenToColorKey(Bitmap surface, int threshold)
    {
        int w = surface.Width, h = surface.Height;
        // 用 Graphics.DrawImage 复制到 32bpp 位图，然后处理 alpha
        var rgb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(rgb)) g.DrawImage(surface, 0, 0);

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rd = rgb.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var resd = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* src = (byte*)rd.Scan0;
            byte* dst = (byte*)resd.Scan0;
            int stride = rd.Stride;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sIdx = y * stride + x * 4;
                    int dIdx = y * w + x;
                    byte r = src[sIdx + 2], g = src[sIdx + 1], b = src[sIdx], a = src[sIdx + 3];
                    dst[dIdx * 4] = b;
                    dst[dIdx * 4 + 1] = g;
                    dst[dIdx * 4 + 2] = r;
                    dst[dIdx * 4 + 3] = (a < threshold || (r == 255 && g == 0 && b == 254)) ? (byte)0 : (byte)255;
                }
            }
        }
        rgb.UnlockBits(rd);
        result.UnlockBits(resd);
        return result;
    }

    static List<(Bitmap Surface, Vector2 Offset)> RotationFrames(Bitmap sprite, int count)
    {
        count = Math.Max(8, count);
        var frames = new List<(Bitmap, Vector2)>();
        float step = 360.0f / count;
        for (int i = 0; i < count; i++)
        {
            float deg = step * i;
            int pad = 20;
            using (var rotated = new Bitmap(sprite.Width + pad * 2, sprite.Height + pad * 2, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(rotated))
            {
                g.TranslateTransform(rotated.Width / 2f, rotated.Height / 2f);
                g.RotateTransform(deg);
                g.TranslateTransform(-sprite.Width / 2f, -sprite.Height / 2f);
                g.DrawImage(sprite, 0, 0);
                var bounds = new RectangleF(0, 0, rotated.Width, rotated.Height);
                int cx = (int)bounds.X, cy = (int)bounds.Y, cw = (int)bounds.Width, ch = (int)bounds.Height;
                if (cw <= 0 || ch <= 0) continue;
                using (var piece = rotated.Clone(new Rectangle(cx, cy, cw, ch), PixelFormat.Format32bppArgb))
                {
                    Bitmap flat = FlattenToColorKey(piece, 96);
                    frames.Add((flat, new Vector2(cx + cw / 2f - rotated.Width / 2f, cy + ch / 2f - rotated.Height / 2f)));
                }
            }
        }
        return frames;
    }

    static bool[,] TrimLabelTail(bool[,] mask, int w, int h, float minKeep = 0.6f)
    {
        var rows = new bool[h];
        for (int y = 0; y < h; y++) { rows[y] = false; for (int x = 0; x < w; x++) if (mask[y, x]) { rows[y] = true; break; } }
        int top = -1, bottom = -1;
        for (int y = 0; y < h; y++) if (rows[y]) { top = y; break; }
        for (int y = h - 1; y >= 0; y--) if (rows[y]) { bottom = y; break; }
        if (top < 0 || bottom < 0) return mask;
        int height = bottom - top + 1;
        if (height < 8) return mask;
        int run = 0;
        for (int y = top; y <= bottom; y++)
        {
            if (rows[y]) { run = 0; continue; }
            run++;
            if (run >= 2)
            {
                int cut = y - run + 1;
                int kept = cut - top, removed = bottom - cut + 1;
                if (kept / (float)height >= minKeep && removed / (float)height <= 1.0f - minKeep)
                {
                    for (int yy = cut; yy < h; yy++)
                        for (int x = 0; x < w; x++) mask[yy, x] = false;
                }
                break;
            }
        }
        return mask;
    }

    // 用 PrintWindow 把窗口内容渲染到位图。
    // 注意：必须用 flag=0（不能用 PW_RENDERFULLCONTENT）——后者走 DWM 缓存，
    // 只会渲染主显示器且忽略子窗口的显示/隐藏状态；flag=0 会请求窗口完整重绘，
    // 从而正确渲染所有显示器上的桌面图标。未覆盖像素为纯黑，可直接当透明背景。
    static Bitmap CaptureWindow(IntPtr hwnd, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { Win32.PrintWindow(hwnd, hdc, 0); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    public static List<(int Item, Point Center, Win32.POINT HomeClient, float HitRadius, List<(Bitmap Surface, Vector2 Offset)> Frames)> ExtractIcons(
        DesktopIcons icons, int fallbackSize, int margin, int threshold, int angles)
    {
        var layout = icons.Layout(fallbackSize);
        if (layout.Count == 0) { Console.WriteLine("[警告] 桌面上没有图标。"); return new(); }

        // 直接渲染桌面图标列表控件（PrintWindow）：即使桌面被其它窗口盖住也能采集，
        // 未被图标覆盖的像素为纯黑，因此无需再做“隐藏/显示”两次截图差分。
        IntPtr capture = icons.Hwnd;
        if (capture == IntPtr.Zero) { Console.WriteLine("[警告] 未找到桌面图标列表。"); return new(); }
        if (!Win32.GetClientRect(capture, out Win32.RECT cr)) { Console.WriteLine("[警告] 无法获取桌面图标层尺寸。"); return new(); }
        int capW = cr.Right - cr.Left, capH = cr.Bottom - cr.Top;
        if (capW < 8 || capH < 8) { Console.WriteLine("[警告] 桌面图标层尺寸异常。"); return new(); }

        Win32.POINT capOrigin = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(capture, ref capOrigin);

        Bitmap fg = CaptureWindow(capture, capW, capH);

        if (Debug)
        {
            Console.WriteLine($"[DBG] capture hwnd=0x{capture.ToInt64():X} size={capW}x{capH} origin=({capOrigin.X},{capOrigin.Y})");
            try
            {
                string tmp = System.IO.Path.GetTempPath();
                fg.Save(System.IO.Path.Combine(tmp, "df_fg.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { Console.WriteLine("[DBG] save failed: " + ex.Message); }
        }

        var sprites = new List<(int, Point, Win32.POINT, float, List<(Bitmap, Vector2)>)>();
        foreach (var (item, center, rect, clientCenter) in layout)
        {
            // 裁剪框（屏幕坐标）与采集位图（以 capOrigin 为原点）坐标转换
            int x1 = Math.Max(capOrigin.X, rect.X - margin);
            int y1 = Math.Max(capOrigin.Y, rect.Y - margin);
            int x2 = Math.Min(capOrigin.X + capW, rect.Right + margin);
            int y2 = Math.Min(capOrigin.Y + capH, rect.Bottom + margin);
            if (x2 - x1 < 4 || y2 - y1 < 4) continue;
            int cw = x2 - x1, ch = y2 - y1;
            int cx = x1 - capOrigin.X, cy = y1 - capOrigin.Y;
            using (var cropFg = fg.Clone(new Rectangle(cx, cy, cw, ch), PixelFormat.Format32bppArgb))
            {
                var mask = CutoutVsBlack(cropFg, threshold, cw, ch);
                TrimLabelTail(mask, cw, ch);
                if (Debug) Console.WriteLine($"[DBG] icon {item} crop=({cx},{cy}) {cw}x{ch} mask={CountTrue(mask, cw, ch)}");
                if (!HasAny(mask, cw, ch)) continue;
                var sprite = SpriteFromArrays(cropFg, mask, cw, ch);
                var frames = RotationFrames(sprite, angles);
                if (frames.Count == 0) continue;
                float hitRadius = 0.5f * Math.Min(rect.Width, rect.Height) + 8f;
                sprites.Add((item, center, clientCenter, hitRadius, frames));
            }
        }
        fg.Dispose();
        return sprites;
    }

    static bool HasAny(bool[,] mask, int w, int h) { for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (mask[y, x]) return true; return false; }
}

// --------------------------------------------------------------------------- //
// 小鱼行为
// --------------------------------------------------------------------------- //

internal struct Vector2 { public float X, Y; public Vector2(float x, float y) { X = x; Y = y; } }

internal class Fish
{
    public List<(Bitmap Surface, Vector2 Offset)> Frames { get; }
    public int FramesCount { get; }
    public Rectangle Bounds { get; }
    private Random rng;
    public float AngleStep, Radius;

    // 对应的真实桌面图标：序号 + 它在图标列表里的客户区中心 + 命中半径
    public int ItemIndex;
    public Win32.POINT HomeClient;
    public float HitRadius;

    public float PosX, PosY, Dir, TurnRate, WiggleFreq, WiggleAmp, SwayAmp;
    public float Phase, Cruise, Speed, WanderTimer, DartLeft, DartCooldown;
    public float SwimAngle, Sway;

    public Fish(List<(Bitmap Surface, Vector2 Offset)> frames, Point start, Rectangle bounds, Random rng, float angleStep, float speedScale,
        int itemIndex, Win32.POINT homeClient, float hitRadius)
    {
        Frames = frames; FramesCount = frames.Count; Bounds = bounds; this.rng = rng; AngleStep = angleStep;
        ItemIndex = itemIndex; HomeClient = homeClient; HitRadius = hitRadius;
        float maxD = 0; foreach (var (s, _) in frames) maxD = Math.Max(maxD, Math.Max(s.Width, s.Height));
        Radius = 0.5f * maxD;
        PosX = Math.Clamp(start.X, bounds.Left + Radius, bounds.Right - Radius);
        PosY = Math.Clamp(start.Y, bounds.Top + Radius, bounds.Bottom - Radius);
        Dir = (float)rng.NextDouble() * MathF.PI * 2;
        TurnRate = (float)(rng.NextDouble() * 1.5 + 1.1);
        WiggleFreq = (float)(rng.NextDouble() * 3.0 + 4.5);
        WiggleAmp = (float)(rng.NextDouble() * 0.16 + 0.16);
        SwayAmp = (float)(rng.NextDouble() * 4.0 + 3.0);
        Phase = (float)(rng.NextDouble() * MathF.PI * 2);
        Cruise = (float)(rng.NextDouble() * 55.0 + 40.0) * speedScale;
        Speed = Cruise * (float)rng.NextDouble() * 0.5f + Cruise * 0.5f;
        WanderTimer = (float)(rng.NextDouble() * 1.2 + 0.3);
        DartLeft = 0; DartCooldown = (float)(rng.NextDouble() * 5.5 + 1.5);
        SwimAngle = Dir; Sway = 0;
    }

    // paused = 鼠标悬停：只保留摆尾/摆动，不推进位置与航向，鼠标移开后从原航向继续
    public void Update(float dt, float t, bool paused)
    {
        float wag = WiggleAmp * MathF.Sin(WiggleFreq * t + Phase);
        SwimAngle = Dir + wag;
        Sway = SwayAmp * MathF.Sin(WiggleFreq * t + Phase + MathF.PI / 2);
        if (paused) return;
        DartCooldown -= dt;
        if (DartCooldown <= 0) { DartLeft = (float)(rng.NextDouble() * 0.7 + 0.4); DartCooldown = (float)(rng.NextDouble() * 8.0 + 4.0); }
        DartLeft = Math.Max(0, DartLeft - dt);
        Speed += ((DartLeft > 0 ? 2.6f : 1.0f) * Cruise - Speed) * Math.Min(1.0f, dt * 1.8f);
        WanderTimer -= dt;
        if (WanderTimer <= 0) { WanderTimer = (float)(rng.NextDouble() * 1.3 + 0.7); Dir += (float)(rng.NextDouble() * 1.5 - 0.75); }
        var b = Bounds; float margin = Radius + 90;
        if (PosX < b.Left + margin || PosX > b.Right - margin || PosY < b.Top + margin || PosY > b.Bottom - margin)
            Dir = TurnToward(Dir, MathF.Atan2(b.Top + b.Height / 2 - PosY, b.Left + b.Width / 2 - PosX), TurnRate * dt);
        PosX += MathF.Cos(SwimAngle) * Speed * dt;
        PosY += MathF.Sin(SwimAngle) * Speed * dt;
        ClampInside();
    }

    private void ClampInside()
    {
        var b = Bounds;
        float x = Math.Clamp(PosX, b.Left + Radius, b.Right - Radius);
        float y = Math.Clamp(PosY, b.Top + Radius, b.Bottom - Radius);
        if (x != PosX || y != PosY)
        { Dir = TurnToward(Dir, MathF.Atan2(b.Top + b.Height / 2 - y, b.Left + b.Width / 2 - x), 1.2f); x = Math.Clamp(x, b.Left + Radius, b.Right - Radius); y = Math.Clamp(y, b.Top + Radius, b.Bottom - Radius); }
        PosX = x; PosY = y;
    }

    // 当前帧与它在屏幕上的中心（绘制与命中测试共用，避免两套坐标算分）
    public (int Cx, int Cy, Bitmap Frame) DrawCenter()
    {
        float deg = (-RadToDeg(SwimAngle)) % 360; if (deg < 0) deg += 360;
        int idx = (int)Math.Round(deg / AngleStep) % FramesCount;
        var (frame, offset) = Frames[idx];
        float nx = MathF.Cos(SwimAngle + MathF.PI / 2), ny = MathF.Sin(SwimAngle + MathF.PI / 2);
        return ((int)(PosX + nx * Sway + offset.X), (int)(PosY + ny * Sway + offset.Y), frame);
    }

    public void Draw(Graphics g, float t)
    {
        var (cx, cy, frame) = DrawCenter();
        var rect = new Rectangle(cx - frame.Width / 2, cy - frame.Height / 2, frame.Width, frame.Height);
        g.DrawImage(frame, rect);
    }

    private static float TurnToward(float cur, float tgt, float max)
    {
        float d = (tgt - cur + MathF.PI) % (MathF.PI * 2) - MathF.PI;
        return Math.Abs(d) <= max ? cur + d : cur + MathF.Sign(d) * max;
    }
    private static float RadToDeg(float r) => r * 180.0f / MathF.PI;
}

// --------------------------------------------------------------------------- //
// 自定义异常
// --------------------------------------------------------------------------- //

internal class RuntimeError : Exception { public RuntimeError(string m) : base(m) { } }

// --------------------------------------------------------------------------- //
// 透明覆盖窗口
// --------------------------------------------------------------------------- //

internal class OverlayWindow : IDisposable
{
    private Form _form;
    private System.Windows.Forms.Timer _timer;
    private List<Fish> _fish;
    private Action _onClose;
    private DesktopIcons _icons;
    private float _lastBottom = 0;
    private System.Diagnostics.Stopwatch _sw;
    private Rectangle _virtualBounds;
    private int _ticks = 0;
    private int _paints = 0;
    private bool _closed = false;
    private bool _topmost = false;
    private bool _opaque = false;
    private bool _mouse;

    // 鼠标交互：窗口保持点击穿透，改用低级鼠标钩子"观察"事件；
    // 只有点击落在某条小鱼对应的真实图标上时才吞掉该事件并转交给 explorer。
    private Win32.LowLevelMouseProc? _hookProc;
    private IntPtr _hook = IntPtr.Zero;
    private readonly List<(int Cx, int Cy, int R, Fish Fish)> _hits = new();
    private readonly Dictionary<int, Fish> _byItem = new();
    private Win32.POINT _cursor;
    private IntPtr _progman = IntPtr.Zero, _shell = IntPtr.Zero, _deskRoot = IntPtr.Zero;
    private int _lastDownItem = -1;
    private uint _lastDownMs = 0;

    public IntPtr Hwnd { get; private set; }

    public OverlayWindow(Rectangle virtualBounds, int fps, bool topmost, bool opaque, List<Fish> fish, DesktopIcons icons, bool mouse, Action onClose)
    {
        _fish = fish; _icons = icons; _onClose = onClose; _mouse = mouse;
        _sw = System.Diagnostics.Stopwatch.StartNew();
        _virtualBounds = virtualBounds;
        _topmost = topmost;
        _opaque = opaque;
        foreach (var f in _fish) _byItem[f.ItemIndex] = f;

        _form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            BackColor = opaque ? Color.Blue : Color.FromArgb(Config.CHROMA_R, Config.CHROMA_G, Config.CHROMA_B),
            TransparencyKey = opaque ? Color.Empty : Color.FromArgb(Config.CHROMA_R, Config.CHROMA_G, Config.CHROMA_B),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = virtualBounds.Location,
            Size = virtualBounds.Size,
            TopMost = topmost,
        };
        _form.CreateControl();
        Hwnd = _form.Handle;

        MakeClickThrough();
        if (!_topmost) PlaceOnDesktop();
        if (_mouse) StartMouse();

        int interval = Math.Max(16, 1000 / Math.Max(15, fps));
        _timer = new System.Windows.Forms.Timer { Interval = interval };
        _timer.Tick += OnTick;
        _form.Paint += OnPaint;
    }

    private void StartMouse()
    {
        _progman = Win32.FindWindow("Progman", "Program Manager");
        _shell = Win32.GetShellWindow();
        // 图标列表挂在哪个根窗口下：经典布局是 Progman，开了桌面幻灯片/部分主题时是承载
        // SHELLDLL_DefView 的 WorkerW，所以只能从列表句柄反推，不能写死 Progman。
        _deskRoot = Win32.GetAncestor(_icons.Hwnd, Win32.GA_ROOT);
        _hookProc = OnMouseMessage;   // 必须持有委托引用，否则会被 GC 回收导致崩溃
        _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _hookProc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Console.WriteLine($"[DesktopFish] 警告：鼠标钩子安装失败（错误 {Marshal.GetLastWin32Error()}），悬停与点击不可用。");
            return;
        }
        if (IconExtractor.Debug)
            Console.WriteLine($"[DBG] 桌面判定：deskRoot=0x{_deskRoot.ToInt64():X} progman=0x{_progman.ToInt64():X} shell=0x{_shell.ToInt64():X} overlay=0x{Hwnd.ToInt64():X}");
        Console.WriteLine("[DesktopFish] 鼠标交互：光标停在某条小鱼上它会原地摆尾；双击=打开该图标；右键=该图标的系统菜单。");
    }

    private void MakeClickThrough()
    {
        uint ex = Win32.GetWindowLong(Hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLong(Hwnd, Win32.GWL_EXSTYLE, ex);
        Win32.SetWindowPos(Hwnd, new IntPtr(0), 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        // 透明由 WinForms 的 TransparencyKey 处理（自动使用 LWA_COLORKEY），不要再手动设置 WS_EX_LAYERED，
        // 否则两者冲突会导致窗口整体不可见。
    }

    // 放到桌面窗口（Progman）正上方：位于壁纸之上、普通窗口之下。
    // 不能用 HWND_BOTTOM——那会落到壁纸/桌面窗口下面而被完全遮住；
    // 也不使用 WorkerW 重挂载——不同系统上会被壁纸遮住。
    private void PlaceOnDesktop()
    {
        IntPtr progman = Win32.FindWindow("Progman", "Program Manager");
        IntPtr insertAfter = progman != IntPtr.Zero ? progman : new IntPtr((int)Win32.HWND_BOTTOM);
        Win32.SetWindowPos(Hwnd, insertAfter, 0, 0, _form.Width, _form.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        _lastBottom = _sw.ElapsedMilliseconds / 1000f;
    }

    private void KeepBottommost()
    {
        if (_topmost) return;
        float now = _sw.ElapsedMilliseconds / 1000f;
        if (now - _lastBottom >= 1f)
        {
            try
            {
                IntPtr progman = Win32.FindWindow("Progman", "Program Manager");
                IntPtr insertAfter = progman != IntPtr.Zero ? progman : new IntPtr((int)Win32.HWND_BOTTOM);
                Win32.SetWindowPos(Hwnd, insertAfter, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            catch { }
            _lastBottom = now;
        }
    }

    private void CloseOnce()
    {
        if (_closed) return;
        _closed = true;
        _onClose();
    }

    // ---- 鼠标：命中判定 / 悬停 / 事件转交 ----------------------------------- //

    // 该屏幕点是否露着桌面（没有被普通窗口、任务栏盖住）
    private bool IsDesktopLayer(Win32.POINT pt)
    {
        IntPtr w = Win32.WindowFromPoint(pt);
        if (w == IntPtr.Zero) return false;
        IntPtr root = Win32.GetAncestor(w, Win32.GA_ROOT);
        return root == Hwnd || root == _progman || root == _shell || root == _deskRoot;
    }

    // 小鱼当前画在屏幕上的圆形命中区（每帧重建，钩子回调与本方法同线程，无需加锁）
    private void RebuildHits()
    {
        _hits.Clear();
        foreach (var f in _fish)
        {
            var (cx, cy, _) = f.DrawCenter();
            _hits.Add((cx, cy, (int)f.HitRadius, f));
        }
    }

    private Fish? FishAt(Win32.POINT pt)
    {
        foreach (var (cx, cy, r, f) in _hits)
        {
            int dx = pt.X - cx, dy = pt.Y - cy;
            if (dx * dx + dy * dy <= r * r) return f;
        }
        return null;
    }

    // 点击是否落在某条小鱼上，并且这条小鱼此刻真的露在桌面上
    private bool ClickOnFish(Win32.POINT pt, out int item)
    {
        item = -1;
        var f = FishAt(pt);
        if (f == null || !IsDesktopLayer(pt)) return false;
        item = f.ItemIndex;
        return true;
    }

    private IntPtr OnMouseMessage(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 钩子回调会阻塞整个系统的鼠标投递，这里只做坐标判定，动作延后到 UI 队列
        if (nCode == Win32.HC_ACTION && _mouse)
        {
            uint msg = (uint)wParam;
            bool isDown = msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONDBLCLK;
            bool isUp = msg == Win32.WM_LBUTTONUP || msg == Win32.WM_RBUTTONUP;
            bool isRDown = msg == Win32.WM_RBUTTONDOWN;
            if (isDown || isUp || isRDown)
            {
                var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                if (ClickOnFish(data.pt, out int item))
                {
                    if (isDown) OnFishPress(item, data.time);
                    else if (msg == Win32.WM_RBUTTONUP) OnFishRightClick(item);
                    return new IntPtr(1);   // 吞掉：桌面不会同时选中图标或弹出空白菜单
                }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // 第二次按下（系统报 DBLCLK）落在同一个图标上 → 转交 explorer 执行默认动作。
    // 第一次按下时光标已悬停在小鱼上，它会停在原地，所以两下容易点对同一个目标。
    private void OnFishPress(int item, uint time)
    {
        uint now = time != 0 ? time : (uint)Environment.TickCount;
        if (item == _lastDownItem && now - _lastDownMs <= Win32.GetDoubleClickTime())
        {
            _lastDownItem = -1;
            if (_byItem.TryGetValue(item, out var f)) Enqueue(() => ForwardDoubleClick(f));
            return;
        }
        _lastDownItem = item;
        _lastDownMs = now;
    }

    private void OnFishRightClick(int item)
    {
        if (_byItem.TryGetValue(item, out var f)) Enqueue(() => ForwardContextMenu(f));
    }

    private void Enqueue(Action a)
    {
        try { _form.BeginInvoke(a); } catch { }
    }

    private void ForwardDoubleClick(Fish f)
    {
        try
        {
            string? name = _icons.ItemName(f.ItemIndex);
            if (name == null) { Console.WriteLine($"[DesktopFish] 双击小鱼 #{f.ItemIndex} 失败：读不到图标名"); return; }
            string? path = ShellLaunch.DesktopItemPath(name);
            if (path == null) { Console.WriteLine($"[DesktopFish] 双击小鱼「{name}」失败：桌面项里没有它"); return; }
            if (ShellLaunch.Open(path)) Console.WriteLine($"[DesktopFish] 双击小鱼「{name}」→ 打开 {path}");
            else Console.WriteLine($"[DesktopFish] 双击小鱼「{name}」失败：系统拒绝打开 {path}");
        }
        catch (Exception ex) { Console.WriteLine($"[DesktopFish] 双击转发失败：{ex.Message}"); }
    }

    private void ForwardContextMenu(Fish f)
    {
        try
        {
            _icons.SelectItem(f.ItemIndex);
            _icons.PostContextMenu(f.ItemIndex, f.HomeClient);
            if (IconExtractor.Debug) Console.WriteLine($"[DesktopFish] 右键小鱼 #{f.ItemIndex} → explorer 原生菜单");
        }
        catch (Exception ex) { Console.WriteLine($"[DesktopFish] 右键转发失败：{ex.Message}"); }
    }

    // 悬停：光标停在谁身上谁就停住（小鱼会游进静止的光标，所以每帧重算）
    private Fish? HoverFish()
    {
        if (_hook == IntPtr.Zero) return null;
        if (Win32.FindWindow("#32768", null) != IntPtr.Zero) return null; // 菜单开着，别抢操作
        Win32.POINT pt = _cursor;
        var f = FishAt(pt);
        return f != null && IsDesktopLayer(pt) ? f : null;
    }

    private void Quit()
    {
        _timer.Stop();
        CloseOnce();
        try { _form.Close(); } catch { }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0 || !Win32.IsWindow(Hwnd))
        {
            if (IconExtractor.Debug) Console.WriteLine($"[DBG] ESC detected (key={Win32.GetAsyncKeyState(Win32.VK_ESCAPE)}, isWindow={Win32.IsWindow(Hwnd)})");
            Quit();
            return;
        }
        if (Win32.GetCursorPos(out Win32.POINT c)) _cursor = c;
        Fish? hover = HoverFish();
        float dt = _timer.Interval / 1000f;
        float t = _sw.ElapsedMilliseconds / 1000f;
        foreach (var f in _fish) f.Update(dt, t, f == hover);
        RebuildHits();
        _form.Invalidate();
        KeepBottommost();
        _icons.EnsureHidden();
        if (IconExtractor.Debug)
        {
            _ticks++;
            if (_ticks % 60 == 0)
            {
                string pos = "";
                foreach (var h in _hits) pos += $" #{h.Fish.ItemIndex}@({h.Cx},{h.Cy})r{h.R}";
                Console.WriteLine($"[DBG] tick {_ticks} paints {_paints} cursor=({_cursor.X},{_cursor.Y}) hover={(hover == null ? "-" : hover.ItemIndex.ToString())}{pos}");
            }
        }
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        _paints++;
        e.Graphics.Clear(_opaque ? Color.Blue : Color.FromArgb(Config.CHROMA_R, Config.CHROMA_G, Config.CHROMA_B));
        float t = _sw.ElapsedMilliseconds / 1000f;
        // 小鱼坐标是虚拟桌面绝对坐标，窗口可能不在 (0,0)，需平移到窗口局部坐标
        e.Graphics.TranslateTransform(-_virtualBounds.X, -_virtualBounds.Y);
        foreach (var f in _fish) f.Draw(e.Graphics, t);
    }

    public void Run()
    {
        _form.FormClosing += (s, e) => { _timer.Stop(); CloseOnce(); };
        _timer.Start();
        Application.Run(_form);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { try { Win32.UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
        _hookProc = null;
        try { _timer?.Dispose(); } catch { }
        try { _form?.Dispose(); } catch { }
    }
}
