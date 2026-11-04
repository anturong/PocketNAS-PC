using System.Runtime.Versioning;
using PocketNAS.Shared;

namespace FtpsServer;

/// <summary>
/// FTPS 服务的管理入口。通用逻辑在 WindowsServiceHelper，
/// 这里补两件 FTPS 特有的事：装完放行端口、卸载时清理防火墙规则。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FtpsServiceHelper : WindowsServiceHelper
{
    public FtpsServiceHelper()
        : base("FtpsService", "FTPS Service", "FTPS 文件传输服务（显式 21 / 隐式 990）", "FtpsService.exe")
    {
    }

    protected override void OnInstalled(string baseDir)
    {
        var options = TryLoadOptions(baseDir);
        if (options != null) FtpsFirewall.Apply(options);
    }

    protected override void OnUninstalling(string baseDir) => FtpsFirewall.Remove();

    /// <summary>服务目录下的 ftps_config.json；不存在时返回 null</summary>
    internal static FtpsOptions? TryLoadOptions(string baseDir)
    {
        var path = Path.Combine(baseDir, "ftps_config.json");
        if (!File.Exists(path)) return null;
        try { return FtpsOptions.Load(path); }
        catch { return null; }
    }
}

/// <summary>
/// 提权子进程的入口：外壳以管理员身份重启自身后，只执行这里对应的动作，不显示界面。
/// 参数与 WebDAV 的 --install/--uninstall/--restart 保持一致的语义，只是加了 ftps 前缀避免冲突。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FtpsSetup
{
    public static void RunElevatedCommand(string command, string[] extraArgs)
    {
        var helper = new FtpsServiceHelper();
        try
        {
            switch (command)
            {
                case "--ftps-install":
                    var baseDir = extraArgs.Length > 0
                        ? extraArgs[0]
                        : AppContext.BaseDirectory.TrimEnd('\\');
                    helper.InstallService(baseDir);
                    break;
                case "--ftps-uninstall":
                    helper.UninstallService();
                    break;
                case "--ftps-restart":
                    helper.RestartService();
                    break;
            }
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "ftps_elevated_error.log"),
                    $"[{DateTime.Now}] {command} 失败: {ex}\n");
            }
            catch { }
            Environment.ExitCode = 1;
        }
    }
}
