using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PocketNAS.Shared;
using PocketNAS.PC.Views;
using WebDAVConfigurator;

namespace PocketNAS.PC;

public partial class MainWindow : Window
{
    private sealed class NavModule
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required string Status { get; init; }
        public required Brush Dot { get; init; }
        public required Brush TitleColor { get; init; }
        public string TagText { get; init; } = "";
        public Visibility TagVisibility { get; init; } = Visibility.Collapsed;
    }

    private readonly ObservableCollection<NavModule> _modules = new();
    private readonly Dictionary<string, UserControl> _views = new();
    private int _logCount;

    public MainWindow()
    {
        InitializeComponent();
        BuildModules();

        NavList.ItemsSource = _modules;
        TxtVersion.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · 模块骨架";

        LogService.Written += OnLogWritten;
        Closed += (_, _) => LogService.Written -= OnLogWritten;

        NavList.SelectedIndex = 0;
        LogService.Success("系统", "PocketNAS-PC 启动完成");

        var shotIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--shot");
        if (shotIndex >= 0 && shotIndex + 1 < Environment.GetCommandLineArgs().Length)
        {
            var path = Environment.GetCommandLineArgs()[shotIndex + 1];
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => RenderShot(path),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void RenderShot(string path)
    {
        LogService.Info("系统", "界面自渲染快照");
        Dispatcher.BeginInvoke(() =>
        {
            var width = (int)Math.Ceiling(ActualWidth);
            var height = (int)Math.Ceiling(ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            Application.Current.Shutdown();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void BuildModules()
    {
        var blue = (Brush)FindResource("Accent.Blue");
        var green = (Brush)FindResource("Accent.Green");
        var hint = (Brush)FindResource("Fg.Hint");
        var muted = (Brush)FindResource("Fg.Muted");

        _modules.Add(new NavModule
        {
            Key = "webdav", Title = "WebDAV", Status = "待接入",
            Subtitle = "把本机目录通过 WebDAV 暴露给 PocketNAS",
            Dot = green, TitleColor = muted
        });
        _modules.Add(new NavModule
        {
            Key = "ssh", Title = "SSH", Status = "待接入",
            Subtitle = "sshd 一键配置、私钥部署与扫码导入",
            Dot = blue, TitleColor = muted
        });
        _modules.Add(new NavModule
        {
            Key = "ftps", Title = "FTPS", Status = "预留",
            Subtitle = "预留模块位，本期不提供实现",
            Dot = hint, TitleColor = hint, TagText = "预留", TagVisibility = Visibility.Visible
        });
        _modules.Add(new NavModule
        {
            Key = "dlna", Title = "DLNA", Status = "预留",
            Subtitle = "预留模块位，本期不提供实现",
            Dot = hint, TitleColor = hint, TagText = "预留", TagVisibility = Visibility.Visible
        });

        _views["webdav"] = new WebDavView();
        _views["ssh"] = new ModulePlaceholder("SSH", "模块视图待迁入",
            "再下一步把 SshHelper 的「连接信息 / 私钥管理 / 扫码导入」三张卡片搬到这里。");
        _views["ftps"] = new ModulePlaceholder("FTPS", "预留模块",
            "位置已留好，等确定 FTPS 服务实现方案后再填充。");
        _views["dlna"] = new ModulePlaceholder("DLNA", "预留模块",
            "位置已留好，等确定 DLNA 媒体库方案后再填充。");
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavModule module) return;

        TxtTitle.Text = module.Title;
        TxtSubtitle.Text = module.Subtitle;
        TxtStatus.Text = module.Status;
        StatusPill.BorderBrush = module.TagVisibility == Visibility.Visible
            ? (Brush)FindResource("Border.Strong")
            : (Brush)FindResource("Accent.Green");
        TxtStatus.Foreground = module.TagVisibility == Visibility.Visible
            ? (Brush)FindResource("Fg.Hint")
            : (Brush)FindResource("Accent.Green");

        ModuleHost.Content = _views.TryGetValue(module.Key, out var view) ? view : null;
        LogService.Info(module.Title, "切换到该模块");
    }

    private void OnLogWritten(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendEntry(entry));
            return;
        }

        AppendEntry(entry);
    }

    private void AppendEntry(LogEntry entry)
    {
        var blue = (Brush)FindResource("Accent.Blue");
        var hint = (Brush)FindResource("Fg.Hint");
        var text = (Brush)FindResource("Fg.Text");

        var messageBrush = entry.Level switch
        {
            LogLevel.Success => (Brush)FindResource("Accent.Green"),
            LogLevel.Warn => (Brush)FindResource("Accent.Amber"),
            LogLevel.Error => (Brush)FindResource("Accent.Red"),
            _ => text
        };

        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
        paragraph.Inlines.Add(new Run($"[{entry.Time:HH:mm:ss}] ") { Foreground = blue });
        paragraph.Inlines.Add(new Run($"{entry.Module} ") { Foreground = hint });
        paragraph.Inlines.Add(new Run(entry.Message) { Foreground = messageBrush });

        LogConsole.Document.Blocks.Add(paragraph);
        _logCount++;
        TxtLogCount.Text = $"共 {_logCount} 条";
        LogConsole.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logCount == 0) return;
        var range = new TextRange(LogConsole.Document.ContentStart, LogConsole.Document.ContentEnd);
        Clipboard.SetText(range.Text);
        LogService.Info("系统", "日志已复制到剪贴板");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogConsole.Document.Blocks.Clear();
        _logCount = 0;
        TxtLogCount.Text = "共 0 条";
    }
}
