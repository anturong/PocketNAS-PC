using System;
using System.Windows;
using PocketNAS.Shared;

namespace PocketNAS.PC.Cast;

/**
 * 独立播放窗口：投送到了才弹，主界面不让位、不内嵌、不抢全屏。
 *
 * 这是「外部播放器不可用」时的兜底窗口（第 18 条）：自发现列表为空、默认失效或启动失败才走这里。
 * 播放内核用 WPF 自带 MediaElement，零第三方依赖、不增打包体积；
 * 代价是只覆盖系统解码器支持的常见格式（mp4 / H.264 / AAC 等），也没有快进 / 进度条。
 * 遇到播不了的会在窗口里给提示与「复制地址」，可交给 VLC 之类外部播放器。
 */
public partial class CastPlayerWindow : Window
{
    private readonly CastRequest _req;
    private bool _fullscreen;

    public CastPlayerWindow(CastRequest req, string remote)
    {
        InitializeComponent();
        _req = req;
        var name = string.IsNullOrWhiteSpace(req.Title) ? "未命名" : req.Title;
        Title = "投屏播放 · " + name;
        TxtTitle.Text = name;
        TxtFrom.Text = $"来自 {remote} · 系统播放器（mp4 / H.264 / AAC）";
        try
        {
            Player.Source = new Uri(req.Url, UriKind.Absolute);
            Player.Play();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        => ShowError(e.ErrorException?.Message ?? "未知错误");

    private void ShowError(string reason)
    {
        TxtError.Text = "系统播放器打不开这段媒体：" + reason +
                        "\n可点「复制地址」用 VLC 等外部播放器打开；结束播放直接关掉本窗口，或在手机端再投一个片切换。";
        TxtError.Visibility = Visibility.Visible;
        LogService.Warn("投屏接收", "播放失败：" + reason);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetText(_req.Url);

    private void BtnFull_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            BtnFull.Content = "全屏";
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            BtnFull.Content = "退出全屏";
        }
        _fullscreen = !_fullscreen;
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        Player.Stop();
        Player.Close();
        base.OnClosed(e);
    }
}
