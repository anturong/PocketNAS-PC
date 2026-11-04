using System.Diagnostics;

namespace FtpsServer;

/// <summary>
/// FTPS 的防火墙规则：显式端口、隐式端口、被动端口段。
/// 用安全组统一管理，卸载时按组一次性清理。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class FtpsFirewall
{
    private const string RuleGroup = "PocketNAS FTPS";

    public static void Apply(FtpsOptions options)
    {
        Remove();

        AddRule("显式 FTPS", options.ExplicitPort.ToString());
        AddRule("隐式 FTPS", options.ImplicitPort.ToString());

        // 被动模式端口段可能被客户端用于数据连接
        if (options.PasvMax >= options.PasvMin)
            AddRule("FTPS 被动端口", $"{options.PasvMin}-{options.PasvMax}");
    }

    public static void Remove()
        => Run($"advfirewall firewall delete rule group=\"{RuleGroup}\"");

    private static void AddRule(string title, string localPort)
        => Run($"advfirewall firewall add rule name=\"{RuleGroup} ({title})\" group=\"{RuleGroup}\" " +
               $"dir=in action=allow protocol=TCP localport={localPort} profile=any enable=yes");

    private static void Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit(8000);
        }
        catch
        {
            // 防火墙配置失败不应阻断服务安装，具体错误由服务日志体现
        }
    }
}
