using BoseApp.Tray;
using Microsoft.UI.Xaml;

namespace BoseApp;

public partial class App : Application
{
    private const string MutexName = "BoseApp.SingleInstance.{8C3F4E5A-6D71-4C82-9E33-2D3A4F5B6C73}";

    private Window? _window;
    private Mutex? _mutex;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：已有实例在运行时，让它显示主窗口，然后退出自己
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            TrayIcon.SignalExistingInstance();
            Exit();
            return;
        }

        // 开机自启动时带 --tray 参数，只显示托盘图标不弹出窗口
        bool startInTray = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow();
        _window = window;
        if (!startInTray) window.Activate();
    }
}
