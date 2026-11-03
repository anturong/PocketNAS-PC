using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SshHelper;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath)!, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底落盘：曾经出现「启动就退、什么日志都没留下」的情况，只能靠这个定位
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            WriteLog("AppDomain.UnhandledException: " + a.ExceptionObject);
        DispatcherUnhandledException += (_, a) =>
        {
            WriteLog("DispatcherUnhandledException: " + a.Exception);
            MessageBox.Show(a.Exception.ToString(), "SshHelper 异常");
            a.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            WriteLog("UnobservedTaskException: " + a.Exception);
            a.SetObserved();
        };
        WriteLog("==== 启动 ====");
        base.OnStartup(e);
    }

    private static void WriteLog(string text)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch
        {
            // 日志写不进去就写不进去，别再抛
        }
    }
}
