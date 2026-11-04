using System.Diagnostics;
using System.IO;
using PocketNAS.Shared;

namespace WebDAVConfigurator;

/// <summary>
/// WebDAV 服务的管理入口：通用逻辑在 WindowsServiceHelper，
/// 这里只补 WebDAV 特有的两件事——放行端口、卸载时清理证书绑定。
/// </summary>
public class ServiceHelper : WindowsServiceHelper
{
    public ServiceHelper()
        : base("WebDAVService", "WebDAV Service", "WebDAV 文件共享服务", "WebDAVService.exe")
    {
    }

    protected override void OnInstalled() => ApplyFirewallRules();

    protected override void OnUninstalling()
    {
        // 清理 HTTPS 证书绑定
        try
        {
            var cfg = WebDAVConfigData.Load();
            if (cfg.EnableHttps)
            {
                var psi = new ProcessStartInfo("netsh", $"http delete sslcert ipport=0.0.0.0:{cfg.HttpsPort}")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(5000);
            }
        }
        catch { }

        WriteFirewallLog(FirewallHelper.RemoveRules());
    }

    private static void ApplyFirewallRules()
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webdav_config.json");
        if (!File.Exists(configPath)) return;
        try
        {
            var config = WebDAVConfigData.Load();
            WriteFirewallLog(FirewallHelper.EnsureRules(config));
        }
        catch (Exception ex)
        {
            WriteFirewallLog(new List<(bool, string)> { (false, $"防火墙规则配置异常: {ex.Message}") });
        }
    }

    private static void WriteFirewallLog(List<(bool success, string message)> results)
    {
        if (results.Count == 0) return;
        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var lines = results.Select(r =>
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(r.success ? "OK" : "FAIL")} {r.message}");
            File.AppendAllLines(Path.Combine(dir, "firewall.log"), lines);
        }
        catch { }
    }
}
