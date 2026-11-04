using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PocketNAS.FtpsService;
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
Log.Information("FTPS 服务启动中...");
Log.Information("日志文件路径: {LogFile}", logFile);
Log.Information("配置目录: {BaseDir}", baseDir);

try
{
    var builder = Host.CreateDefaultBuilder(args);
    builder.UseWindowsService(options =>
    {
        options.ServiceName = "FtpsService";
    });
    builder.ConfigureServices(services =>
    {
        services.AddHostedService<FtpsWorker>();
    });

    var host = builder.Build();
    Log.Information("主机已构建，正在运行...");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "FTPS 服务异常终止");
}
finally
{
    Log.CloseAndFlush();
}
