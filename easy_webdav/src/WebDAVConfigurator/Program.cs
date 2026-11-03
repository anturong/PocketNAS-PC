using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using WebDAVConfigurator;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--"))
        {
            RunElevatedCommand(args[0], args.Skip(1).ToArray());
            return;
        }

        var app = new System.Windows.Application();
        app.Run(new MainWindow());
    }

    private static void RunElevatedCommand(string command, string[] extraArgs)
    {
        var helper = new ServiceHelper();
        try
        {
            switch (command)
            {
                case "--install":
                    var baseDir = extraArgs.Length > 0
                        ? extraArgs[0]
                        : AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                    helper.InstallService(baseDir);
                    break;
                case "--uninstall":
                    helper.UninstallService();
                    break;
                case "--restart":
                    helper.RestartService();
                    break;
            }
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "webdav_elevated_error.log"),
                $"[{DateTime.Now}] {command} 失败: {ex}\n");
            Environment.ExitCode = 1;
        }
    }

    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RelaunchAsAdmin(string arguments)
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null) return false;
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var process = Process.Start(startInfo);
            if (process != null)
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            return false;
        }
        catch { return false; }
    }
}
