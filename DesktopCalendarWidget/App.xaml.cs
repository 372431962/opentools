using System.Threading;
using System.Windows;

namespace DesktopCalendarWidget;

public partial class App : Application
{
    private Mutex? singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstance.MutexName, out var createdNew);
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

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstanceMutex?.Dispose();
        singleInstanceMutex = null;
        base.OnExit(e);
    }
}
