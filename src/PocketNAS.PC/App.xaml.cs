using System.IO;
using System.Windows;
using System.Windows.Threading;
using PocketNAS.PC.Cast;
using PocketNAS.Shared;

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

        // 提权子进程：外壳以管理员权限重启自身时，只执行对应模块的服务操作，不显示界面
        if (e.Args.Length > 0)
        {
            switch (e.Args[0])
            {
                case "--install":
                case "--uninstall":
                case "--restart":
                    WebDAVConfigurator.Program.RunElevatedCommand(e.Args[0], e.Args.Skip(1).ToArray());
                    Shutdown();
                    return;

                case "--ftps-install":
                case "--ftps-uninstall":
                case "--ftps-restart":
                    FtpsServer.FtpsSetup.RunElevatedCommand(e.Args[0], e.Args.Skip(1).ToArray());
                    Shutdown();
                    return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 投屏接收默认随主程序启动：手机点投送时 PC 就该有反应，不能要求用户先切到那一页。
        // 用户可在「投屏接收」页里点「停止接收」关掉；关闭主窗口由 OnExit 一并停掉。
        try
        {
            CastReceiver.Instance.StartDefault();
        }
        catch (Exception ex)
        {
            LogService.Warn("投屏接收", "自动启动失败：" + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 关掉主窗口即停止接收，不留后台监听（本版不做托盘常驻）。
        // 外部播放器也一并关：只关本程序自己启动的那个进程，不误杀用户原本开着的窗口。
        CastPlayerLauncher.CloseCurrent();
        CastReceiver.Instance.Stop();
        base.OnExit(e);
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
