using Microsoft.Extensions.Hosting;
using Serilog;

public class WebDAVWorker : BackgroundService
{
    private WebDAVServer? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var config = WebDAVConfig.Load();
            Log.Information("WebDAV 服务正在启动...");
            Log.Information("配置: Host={Host}, HTTP={EnableHttp}({Port}), HTTPS={EnableHttps}({HttpsPort}), RootDir={RootDir}",
                config.Host, config.EnableHttp, config.Port, config.EnableHttps, config.HttpsPort, config.RootDir);

            _server = new WebDAVServer(config);
            await _server.StartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "WebDAV 服务异常终止");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("WebDAV 服务正在停止...");
        if (_server != null)
            await _server.StopAsync();
        await base.StopAsync(cancellationToken);
        Log.Information("WebDAV 服务已停止");
    }
}
