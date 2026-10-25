using System.IO;
using System.Threading;
using System.Windows;

namespace ACML.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var testData = Environment.GetEnvironmentVariable("ACML_DEMO_DATA_DIR");
        var suffix = string.IsNullOrEmpty(testData) ? "" : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(testData)));
        instance = new Mutex(true, @"Local\ACML-Demo-2-0" + suffix, out var created);
        if (!created) { LauncherAlert.Show("ACML","ACML 已经运行，请切换到现有窗口。"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            if (MainWindow is MainWindow window) window.Report(args.Exception);
            else LauncherAlert.Show("ACML 启动失败",args.Exception.Message);
            args.Handled = true;
        };
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { LauncherAlert.Show("ACML 启动失败",ex.Message + "\n\n原配置保留，启动器不会覆盖它。"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
