using System.Windows;

namespace PocketNAS.PC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}
