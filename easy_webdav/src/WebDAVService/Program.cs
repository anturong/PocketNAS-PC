using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
var logDir = Path.Combine(baseDir, "logs");
Directory.CreateDirectory(logDir);
var logFile = Path.Combine(logDir, "service.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.File(logFile,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true)
    .CreateLogger();

Log.Information("============================================================");
Log.Information("WebDAV 服务启动中...");
Log.Information("日志文件路径: {LogFile}", logFile);

try
{
    var builder = Host.CreateDefaultBuilder(args);
    builder.UseWindowsService(options =>
    {
        options.ServiceName = "WebDAVService";
    });
    builder.ConfigureServices(services =>
    {
        services.AddHostedService<WebDAVWorker>();
    });

    var host = builder.Build();
    Log.Information("主机已构建，正在运行...");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WebDAV 服务异常终止");
}
finally
{
    Log.CloseAndFlush();
}
