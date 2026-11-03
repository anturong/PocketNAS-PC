using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PocketNAS.PC;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(AppContext.BaseDirectory, "pocketnas-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            WriteCrash("AppDomain.UnhandledException: " + a.ExceptionObject);
        DispatcherUnhandledException += (_, a) =>
        {
            WriteCrash("DispatcherUnhandledException: " + a.Exception);
            MessageBox.Show(a.Exception.ToString(), "PocketNAS-PC 异常");
            a.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            WriteCrash("UnobservedTaskException: " + a.Exception);
            a.SetObserved();
        };

        // 提权子进程：外壳以管理员权限重启自身时，只执行服务操作，不显示界面
        if (e.Args.Length > 0 && e.Args[0] is "--install" or "--uninstall" or "--restart")
        {
            WebDAVConfigurator.Program.RunElevatedCommand(e.Args[0], e.Args.Skip(1).ToArray());
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void WriteCrash(string text)
    {
        try
        {
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch
        {
            // 日志写不进去就写不进去，别再抛
        }
    }
}
