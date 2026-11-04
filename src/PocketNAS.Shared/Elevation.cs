using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PocketNAS.Shared;

/// <summary>管理员权限判断与自我提权重启，两个模块共用。</summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>以管理员身份重启当前进程并附带参数，等待其结束后返回是否成功</summary>
    public static bool RelaunchAsAdmin(string arguments)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var process = Process.Start(startInfo);
            if (process == null) return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
