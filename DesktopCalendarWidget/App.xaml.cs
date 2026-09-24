using System.Threading;
using System.Windows;

namespace DesktopCalendarWidget;

public partial class App : Application
{
    private Mutex? singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 语言必须在任何界面文案被取用之前定下来，因此这里先读一次配置。
        Loc.Apply(new SettingsService().Load().Language);

        singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstance.MutexName, out _);
        bool createdNew;
        try
        {
            createdNew = singleInstanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例被强杀或崩溃退出时，内核会把互斥体标记为弃用并把它交给本实例。
            // 此时本实例已经是唯一持有者，按“首个实例”继续，否则会变成启动即崩溃。
            createdNew = true;
        }
        if (!createdNew)
        {
            // 已有实例在运行：请它把挂件显示到最前，然后本实例直接退出。
            SingleInstance.NotifyExistingInstance();
            singleInstanceMutex.Dispose();
            singleInstanceMutex = null;
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 换语言后重建主窗口。XAML 里的 x:Static 只在解析时取一次文案，改区域性不会刷新已建好的界面，
    /// 只能重开。进程不重启，因此单实例互斥量与托盘的注册/释放都走各自的正常路径。
    /// </summary>
    public void RestartForNewLanguage(string languageTag)
    {
        // 最后一扇窗关闭会立即退出进程，先切到显式关闭再拆窗口。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 静态 XAML 文案需要在新语言下重新解析；翻译窗自行保留内容与窗口状态。
        var carriedTranslateWindow = (MainWindow as MainWindow)?.DetachTranslateWindow();
        MainWindow?.Close();
        Loc.Apply(languageTag);
        var replacement = carriedTranslateWindow?.RecreateForNewLanguage();
        var window = new MainWindow();
        MainWindow = window;
        window.AdoptTranslateWindow(replacement);
        window.Show();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstanceMutex?.Dispose();
        singleInstanceMutex = null;
        base.OnExit(e);
    }
}
