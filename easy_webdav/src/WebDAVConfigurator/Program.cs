using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace WebDAVConfigurator;

/// <summary>
/// 服务安装/卸载/重启的提权通道。
/// 独立运行时装在 WebDAVConfigurator.exe 里；并入 PocketNAS-PC 后由外壳转发同名参数。
/// </summary>
public static class Program
{
    /// <summary>提权时启动的可执行文件。为空表示重启当前进程（独立运行时的行为）。</summary>
    public static string? ElevationHelperExe { get; set; }

    public static void RunElevatedCommand(string command, string[] extraArgs)
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
            var exe = ElevationHelperExe ?? Process.GetCurrentProcess().MainModule?.FileName;
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
