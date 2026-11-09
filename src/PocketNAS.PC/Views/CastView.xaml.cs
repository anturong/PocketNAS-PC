using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PocketNAS.PC.Cast;
using PocketNAS.Shared;

namespace PocketNAS.PC.Views;

/** 左栏第 4 项「投屏接收」的设置页（原 DLNA 预留占位）。 */
public partial class CastView : UserControl
{
    private readonly CastReceiver _receiver = CastReceiver.Instance;
    private CastPlayerWindow? _player;
    private List<PlayerInfo> _players = new();
    private bool _started;

    public CastView()
    {
        InitializeComponent();
        _receiver.CastRequested += OnCastRequested;
        _receiver.StopRequested += OnStopRequested;
        // 静态事件：取消再订阅，避免页面被重建时重复挂回调。
        CastPlayerLauncher.ActiveExited -= OnPlayerExited;
        CastPlayerLauncher.ActiveExited += OnPlayerExited;
        Loaded += (_, _) =>
        {
            if (!_started)
            {
                _started = true;
                FillNics();
                FillPlayers();
                // App 启动时会自启；这里是自动启动失败后的兜底重试。
                if (!_receiver.IsRunning) StartReceiver();
            }
            Refresh();
        };
    }

    private void StartReceiver()
    {
        if (_receiver.IsRunning)
        {
            LogService.Info("投屏接收", $"服务已在运行：{_receiver.DescriptionUrl}");
            return;
        }
        try
        {
            _receiver.StartDefault();
            SyncNicSelection();
            LogService.Success("投屏接收", $"已就绪：{_receiver.DescriptionUrl}");
        }
        catch (Exception ex)
        {
            LogService.Error("投屏接收", "启动失败：" + ex.Message);
            TxtState.Text = "启动失败：" + ex.Message;
        }
    }

    /** 与 WebDAV / SSH / FTPS 共用一套网卡排序：ComboBoxItem.Tag 存纯 IP。 */
    private void FillNics()
    {
        CmbNic.Items.Clear();
        foreach (var a in LanAddresses.Get())
            CmbNic.Items.Add(new ComboBoxItem { Content = a.ToString(), Tag = a.Ip });
        SyncNicSelection();
    }

    private void SyncNicSelection()
    {
        var match = CmbNic.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == _receiver.Ip);
        CmbNic.SelectedItem = (object?)match ?? (CmbNic.Items.Count > 0 ? CmbNic.Items[0] : null);
    }

    private (string Ip, string Nic) SelectedNic()
    {
        var ip = (CmbNic.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var rec = LanAddresses.Get().FirstOrDefault(a => a.Ip == ip);
        return (ip, rec?.Nic ?? ip);
    }

    /** 播放器列表：自发现 + 手动添加，Tag 存 exe 完整路径。 */
    private void FillPlayers()
    {
        _players = CastPlayerLauncher.Discover();
        CmbPlayer.Items.Clear();
        foreach (var p in _players)
            CmbPlayer.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p.Path });

        var def = CastOptions.LoadDefaultPlayer();
        var match = CmbPlayer.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, def, StringComparison.OrdinalIgnoreCase));
        CmbPlayer.SelectedItem = (object?)match ?? (CmbPlayer.Items.Count > 0 ? CmbPlayer.Items[0] : null);
        RefreshPlayer();
    }

    /** 只读缓存 + 配置，不重扫注册表 —— 下拉变更会频繁调 Refresh()。 */
    private void RefreshPlayer()
    {
        var def = CastOptions.LoadDefaultPlayer();
        var path = string.IsNullOrEmpty(def)
            ? _players.FirstOrDefault()?.Path
            : def;
        TxtDefaultPlayer.Text = string.IsNullOrEmpty(path)
            ? "当前默认：未发现任何播放器（将用内置窗口）"
            : $"当前默认：{Path.GetFileName(path)}";

        if (string.IsNullOrEmpty(def))
            TxtPlayerNotice.Text = _players.Count > 0
                ? "未指定默认，暂用列表第一项；点「设为默认」可固定"
                : "可点「手动指定程序位置…」添加播放器";
        else if (!File.Exists(def))
            TxtPlayerNotice.Text = "原默认已失效，暂用列表第一项；请重新指定默认";
        else
            TxtPlayerNotice.Text = "已记住你指定的默认播放器";
    }

    private void Refresh()
    {
        TxtState.Text = _receiver.IsRunning ? "运行中" : "已停止";
        TxtAddress.Text = _receiver.IsRunning ? _receiver.DescriptionUrl : "—";
        TxtPort.Text = _receiver.IsRunning ? $"当前端口：{_receiver.Port}" : "当前端口：—";
        TxtPortNotice.Text = _receiver.PortNotice;
        var hint = TryFindResource("Fg.Hint") as Brush ?? Brushes.Gray;
        TxtPortNotice.Foreground = _receiver.PortNoticeLevel is LogLevel.Warn or LogLevel.Error
            ? Brushes.OrangeRed
            : hint;
        BtnToggle.Content = _receiver.IsRunning ? "停止接收" : "启用接收";
        ChkConfirm.IsChecked = _receiver.RequireConfirm;
        TxtCurrentNic.Text = _receiver.IsRunning
            ? $"当前：{_receiver.Ip} · {_receiver.NicName}"
            : "当前：—（未启动）";

        var selected = (CmbNic.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        TxtPending.Text = _receiver.IsRunning && selected.Length > 0 && selected != _receiver.Ip
            ? "有未应用的更改：点「应用」后生效（会重启接收服务）"
            : "";
        RefreshPlayer();
    }

    /** 下拉只更新提示，绝不立即生效 —— 变更必须等用户点「应用」。 */
    private void CmbNic_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    /** 播放器下拉同理：只提示，不擅自改默认。 */
    private void CmbPlayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbPlayer.SelectedItem is not ComboBoxItem item) return;
        TxtPlayerNotice.Text = $"已选中 {item.Content}，点「设为默认」后生效并记住";
    }

    private void BtnSetDefault_Click(object sender, RoutedEventArgs e)
    {
        var path = (CmbPlayer.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, "列表为空，请先点「手动指定程序位置…」", "投屏接收");
            return;
        }
        CastOptions.SaveDefaultPlayer(path);
        LogService.Success("投屏接收", $"默认播放器已设为：{Path.GetFileName(path)}（已写入 cast_config.json）");
        FillPlayers();
    }

    private void BtnPickPlayer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择播放程序（.exe）",
            Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dlg.FileName;
        if (!CastPlayerLauncher.IsExe(path) || !File.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, "请选择一个存在的 .exe 文件", "投屏接收");
            return;
        }
        CastOptions.AddManualPlayer(path);
        LogService.Success("投屏接收", $"手动添加播放器：{path}");
        FillPlayers();
        var added = CmbPlayer.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, path, StringComparison.OrdinalIgnoreCase));
        if (added is not null) CmbPlayer.SelectedItem = added;
        TxtPlayerNotice.Text = $"已添加 {Path.GetFileName(path)}；点「设为默认」后才会用它";
    }

    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_receiver.IsRunning)
        {
            _receiver.Stop();
            ClosePlayback();
            TxtNow.Text = "暂无";
        }
        else
        {
            StartReceiver();
        }
        Refresh();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var (ip, nic) = SelectedNic();
        if (ip.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "请先选择一块网卡", "投屏接收");
            return;
        }
        if (_receiver.IsRunning && ip == _receiver.Ip)
        {
            LogService.Info("投屏接收", "网卡未变更，无需重启");
            Refresh();
            return;
        }
        if (!ConfirmInterrupt("切换网卡会中断当前投屏，是否继续？")) return;

        var oldIp = _receiver.Ip;
        var oldPort = _receiver.Port;
        Restart(ip, nic, oldPort, CastReceiver.StartReason.ApplyNic);
        if (_receiver.IsRunning)
        {
            var changed = _receiver.Port != oldPort;
            var msg = changed
                ? $"网卡已切换：{oldIp} → {ip} · {nic}，端口 {oldPort} → {_receiver.Port}，服务已重启"
                : $"网卡已切换：{oldIp} → {ip} · {nic}，服务已重启";
            _receiver.SetNotice(msg, changed ? LogLevel.Warn : LogLevel.Info);
        }
        Refresh();
    }

    private void BtnDetect_Click(object sender, RoutedEventArgs e)
    {
        if (!_receiver.IsRunning)
        {
            LogService.Warn("投屏接收", "接收服务未启动，无法检测空闲端口");
            TxtPortNotice.Text = "接收服务未启动，无法检测空闲端口";
            return;
        }

        var oldPort = _receiver.Port;
        var free = CastReceiver.FindFreePort(oldPort);
        if (free < 0)
        {
            _receiver.SetNotice(
                $"{CastReceiver.FirstPort}–{CastReceiver.LastPort} 无空闲端口，保持当前端口 {oldPort}",
                LogLevel.Error);
            Refresh();
            return;
        }
        if (!ConfirmInterrupt("重新检测端口会中断当前投屏，是否继续？")) return;

        // 按定案：检测端口不夹带网卡变更，一律用当前已生效的网卡。
        Restart(_receiver.Ip, _receiver.NicName, free, CastReceiver.StartReason.DetectFreePort);
        if (_receiver.IsRunning)
            _receiver.SetNotice($"检测空闲端口：{oldPort} → {_receiver.Port}，服务已重启");
        Refresh();
    }

    private void Restart(string ip, string nic, int preferredPort, CastReceiver.StartReason reason)
    {
        try
        {
            _receiver.Stop();
            ClosePlayback();
            TxtNow.Text = "暂无";
            _receiver.Start(ip, nic, preferredPort, reason);
            LogService.Success("投屏接收", $"已就绪：{_receiver.DescriptionUrl}");
        }
        catch (Exception ex)
        {
            LogService.Error("投屏接收", "启动失败：" + ex.Message);
            TxtState.Text = "启动失败：" + ex.Message;
        }
    }

    /** 正在投屏时只提醒、不硬禁用按钮：用户点「是」照常切换。 */
    private bool ConfirmInterrupt(string message)
    {
        if (!_receiver.HasActiveCast && _player is null && !CastPlayerLauncher.IsPlaying) return true;
        return MessageBox.Show(Window.GetWindow(this)!, message, "投屏接收",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ChkConfirm_Click(object sender, RoutedEventArgs e)
    {
        _receiver.RequireConfirm = ChkConfirm.IsChecked == true;
        LogService.Info("投屏接收", _receiver.RequireConfirm ? "已开启首次确认" : "已关闭首次确认");
    }

    private void OnCastRequested(CastRequest req, string remote)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            TxtNow.Text = $"{req.Title}\n来自 {remote}\n{DateTime.Now:HH:mm:ss}";
            // 连投换片：先关掉上一个（内置窗口或自己启动的播放器进程），否则两个窗口一起播。
            ClosePlayback();
            // 关旧播放会顺手清掉会话标记，这次新投送必须重新置上。
            _receiver.MarkCastStarted();

            var exe = CastPlayerLauncher.ResolveDefault(out var notice);
            if (exe is not null && CastPlayerLauncher.Launch(exe, req.Url))
            {
                TxtNow.Text += $"\n播放器：{Path.GetFileName(exe)} · {notice}";
                RefreshPlayer();
                return;
            }

            LogService.Warn("投屏接收", notice + "，回退内置播放窗口");
            TxtNow.Text += "\n播放器：本程序内置窗口（外部播放器不可用）";
            _player = new CastPlayerWindow(req, remote);
            _player.Closed += (_, _) =>
            {
                _player = null;
                _receiver.MarkCastEnded();
                TxtNow.Text = "暂无（播放窗口已关闭）";
            };
            _player.Show();
            _player.Activate();
        });
    }

    private void OnStopRequested()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ClosePlayback();
            TxtNow.Text = "暂无（手机端已停止）";
        });
    }

    /** 自己启动的播放器被关掉 / 播完退出：清会话标记，避免「应用 / 检测端口」误弹中断提醒。 */
    private void OnPlayerExited()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!CastPlayerLauncher.IsPlaying && _player is null)
            {
                _receiver.MarkCastEnded();
                TxtNow.Text = "暂无（播放器已关闭）";
            }
        });
    }

    /** 关掉当前播放：内置窗口 + 自己启动的外部播放器进程，两者都管。 */
    private void ClosePlayback()
    {
        CastPlayerLauncher.CloseCurrent();
        if (_player is null) return;
        try { _player.Close(); } catch { }
        _player = null;
        _receiver.MarkCastEnded();
    }
}
