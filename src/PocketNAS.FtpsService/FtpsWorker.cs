using FtpsServer;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PocketNAS.FtpsService;

/// <summary>
/// 把 FtpsServerHost 挂在通用主机上：服务启动时开始监听，服务停止时优雅关闭。
/// 配置文件与可执行文件同目录（ftps_config.json），与 WebDAV 服务的约定一致。
/// </summary>
public sealed class FtpsWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "ftps_config.json");

        FtpsOptions options;
        try
        {
            options = FtpsOptions.Load(configPath);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "读取配置失败: {ConfigPath}", configPath);
            throw;
        }

        await using var host = new FtpsServerHost(options, message => Log.Information("{Message}", message));

        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "FTPS 监听失败（端口可能被占用或无权限）");
            throw;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 服务停止
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
