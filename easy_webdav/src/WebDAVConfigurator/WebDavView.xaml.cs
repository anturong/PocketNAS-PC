using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PocketNAS.Shared;
using QRCoder;

// UseWindowsForms 会引入同名的 WinForms 类型，这里显式指回 WPF
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace WebDAVConfigurator;

public partial class WebDavView : System.Windows.Controls.UserControl
{
    private readonly string _baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    private string? _serviceDir;

    /// <summary>
    /// 服务程序所在目录：独立运行时与主程序同目录；并入 PocketNAS-PC 后在 service 子目录下。
    /// </summary>
    private string? ServiceDir => _serviceDir ??= ResolveServiceDir();

    private static string? ResolveServiceDir()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "service"),
                     AppContext.BaseDirectory,
                 })
        {
            if (File.Exists(Path.Combine(dir, "WebDAVService.exe")))
                return dir.TrimEnd('\\');
        }
        return null;
    }
    private WebDAVConfigData _config = null!;
    private Window? _qrWindow;

    public WebDavView()
    {
        InitializeComponent();
        LoadConfig();
    }

    // ==================== 日志与提示 ====================

    private static void Log(string message, LogLevel level = LogLevel.Info)
        => LogService.Write("WebDAV", message, level);

    private void Alert(string message, string title = "提示")
        => MessageBox.Show(Window.GetWindow(this) ?? Application.Current.MainWindow!, message, title);

    // ==================== 配置 ====================

    private void LoadConfig()
    {
        _config = WebDAVConfigData.Load();
        ChkHttp.IsChecked = _config.EnableHttp;
        HttpPortPanel.IsEnabled = _config.EnableHttp;
        TxtPort.Text = _config.Port.ToString();
        TxtRootDir.Text = _config.RootDir;
        ChkHttps.IsChecked = _config.EnableHttps;
        TxtHttpsPort.Text = _config.HttpsPort.ToString();
        HttpsPanel.IsEnabled = _config.EnableHttps;
        RefreshUserList();
        if (LstUsers.Items.Count > 0 && LstUsers.SelectedIndex < 0)
            LstUsers.SelectedIndex = 0;
        RefreshQrIps();
        UpdateServerLink();
        Log("配置加载成功。");
        Log($"配置文件：{WebDAVConfigData.ConfigPath}");
    }

    private void RefreshUserList()
    {
        LstUsers.Items.Clear();
        foreach (var u in _config.Users)
            LstUsers.Items.Add($"{u.Username} [{(u.ReadOnly ? "只读" : "读写")}]");
    }

    private void UpdateServerLink()
    {
        var displayHost = (_config.Host == "+" || _config.Host == "*" || _config.Host == "0.0.0.0") ? "localhost" : _config.Host;
        if (ChkHttp.IsChecked == true)
        {
            var port = int.TryParse(TxtPort.Text, out var p) ? p : _config.Port;
            LnkHttp.Text = $"http://{displayHost}:{port}/";
            LnkHttp.Visibility = Visibility.Visible;
        }
        else
        {
            LnkHttp.Visibility = Visibility.Collapsed;
        }

        if (ChkHttps.IsChecked == true)
        {
            var httpsPort = int.TryParse(TxtHttpsPort.Text, out var hp) ? hp : _config.HttpsPort;
            LnkHttps.Text = $"https://{displayHost}:{httpsPort}/";
            LnkHttps.Visibility = Visibility.Visible;
        }
        else
        {
            LnkHttps.Visibility = Visibility.Collapsed;
        }
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtRootDir.Text = dlg.SelectedPath;
    }

    private void ToggleHttps(object sender, RoutedEventArgs e)
    {
        HttpsPanel.IsEnabled = ChkHttps.IsChecked == true;
        UpdateServerLink();
    }

    private void ToggleHttp(object sender, RoutedEventArgs e)
    {
        HttpPortPanel.IsEnabled = ChkHttp.IsChecked == true;
        UpdateServerLink();
    }

    private void OpenLink(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            try
            {
                Process.Start(new ProcessStartInfo(tb.Text) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"打开浏览器失败: {ex.Message}", LogLevel.Error);
            }
        }
    }

    // ==================== 密码框：眼睛切换 + 随机强密码 ====================

    /// <summary>当前密码值：掩码态取 PasswordBox，明文态取 TextBox</summary>
    private string PasswordValue => PwdPass.Visibility == Visibility.Visible
        ? PwdPass.Password
        : TxtPassPlain.Text;

    private void SetPassword(string value)
    {
        PwdPass.Password = value;
        TxtPassPlain.Text = value;
    }

    private void ClearPassword() => SetPassword("");

    private void TogglePassVisibility(object sender, RoutedEventArgs e)
    {
        var wasMasked = PwdPass.Visibility == Visibility.Visible;

        if (wasMasked)
        {
            TxtPassPlain.Text = PwdPass.Password;
            PwdPass.Visibility = Visibility.Collapsed;
            TxtPassPlain.Visibility = Visibility.Visible;
        }
        else
        {
            PwdPass.Password = TxtPassPlain.Text;
            TxtPassPlain.Visibility = Visibility.Collapsed;
            PwdPass.Visibility = Visibility.Visible;
        }

        // 掩码时显示睁眼（点它看明文），明文时显示划线眼
        var icon = wasMasked ? Visibility.Collapsed : Visibility.Visible;
        EyeOpen.Visibility = icon;
        EyePupil.Visibility = icon;
        EyeSlash.Visibility = wasMasked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GeneratePassword(object sender, RoutedEventArgs e)
    {
        // 去掉易混淆字符：0/O、1/l/I
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const int length = 16;

        var chars = new List<char> { PickChar(upper), PickChar(lower), PickChar(digits), PickChar(symbols) };
        var pool = upper + lower + digits + symbols;
        while (chars.Count < length)
            chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);

        // Fisher-Yates 洗牌：否则前四位永远固定是「大写/小写/数字/符号」
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        SetPassword(new string(chars.ToArray()));

        // 生成后直接明文展示，方便核对与复制
        if (PwdPass.Visibility == Visibility.Visible)
            TogglePassVisibility(this, new RoutedEventArgs());

        Log($"已生成 {length} 位强密码（大小写 + 数字 + 符号）");
    }

    private static char PickChar(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    // ==================== 用户管理 ====================

    private void OnUserSelect(object sender, SelectionChangedEventArgs e)
    {
        if (LstUsers.SelectedIndex < 0) return;
        var text = LstUsers.SelectedItem?.ToString() ?? "";
        var username = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
        var user = _config.Users.FirstOrDefault(u => u.Username == username);
        if (user != null)
        {
            TxtUser.Text = user.Username;
            SetPassword(user.Password);
            ChkReadOnly.IsChecked = user.ReadOnly;
        }
    }

    private void AddUser(object sender, RoutedEventArgs e)
    {
        var username = TxtUser.Text.Trim();
        var password = PasswordValue;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        { Alert("用户名和密码不能为空！"); return; }
        if (_config.Users.Any(u => u.Username == username))
        { Alert("用户已存在！"); return; }

        _config.Users.Add(new UserInfo { Username = username, Password = password, ReadOnly = ChkReadOnly.IsChecked == true });
        RefreshUserList();
        TxtUser.Clear(); ClearPassword(); ChkReadOnly.IsChecked = false;
        Log($"用户 {username} 已添加（未保存）", LogLevel.Warn);
    }

    private void UpdateUser(object sender, RoutedEventArgs e)
    {
        if (LstUsers.SelectedIndex < 0) { Alert("请先选择一个用户。"); return; }
        var text = LstUsers.SelectedItem?.ToString() ?? "";
        var oldName = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
        var password = PasswordValue;
        if (string.IsNullOrEmpty(password)) { Alert("密码不能为空！"); return; }

        var user = _config.Users.FirstOrDefault(u => u.Username == oldName);
        if (user != null)
        {
            user.Password = password;
            user.ReadOnly = ChkReadOnly.IsChecked == true;
            RefreshUserList();
            Log($"用户 {oldName} 信息已更新（未保存）", LogLevel.Warn);
        }
    }

    private void DeleteUser(object sender, RoutedEventArgs e)
    {
        if (LstUsers.SelectedIndex < 0) return;
        var text = LstUsers.SelectedItem?.ToString() ?? "";
        var username = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
        var owner = Window.GetWindow(this);
        var result = owner == null
            ? MessageBox.Show($"确定要删除用户 {username} 吗？", "确认", MessageBoxButton.YesNo)
            : MessageBox.Show(owner, $"确定要删除用户 {username} 吗？", "确认", MessageBoxButton.YesNo);
        if (result == MessageBoxResult.Yes)
        {
            _config.Users.RemoveAll(u => u.Username == username);
            RefreshUserList();
            Log($"用户 {username} 已删除（未保存）", LogLevel.Warn);
        }
    }

    // ==================== 保存与服务控制 ====================

    private void SaveConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.EnableHttp = ChkHttp.IsChecked == true;
            _config.Port = int.Parse(TxtPort.Text);
            _config.RootDir = TxtRootDir.Text;
            _config.EnableHttps = ChkHttps.IsChecked == true;
            _config.HttpsPort = int.Parse(TxtHttpsPort.Text);

            if (!_config.EnableHttp && !_config.EnableHttps)
            {
                Alert("至少启用 HTTP 或 HTTPS 其中之一。");
                return;
            }

            _config.Save();
            UpdateServerLink();
            Log("配置已保存到文件。", LogLevel.Success);

            if (Program.IsRunningAsAdmin())
            {
                foreach (var (ok, msg) in FirewallHelper.EnsureRules(_config))
                    Log(msg, ok ? LogLevel.Success : LogLevel.Error);
            }
            else
            {
                var ports = new List<int>();
                if (_config.EnableHttp) ports.Add(_config.Port);
                if (_config.EnableHttps) ports.Add(_config.HttpsPort);
                Log($"防火墙规则需管理员权限，安装服务时会自动放行端口 {string.Join("/", ports)}", LogLevel.Warn);
            }

            Alert("配置已保存！\n请重启服务使配置生效。", "成功");
        }
        catch (Exception ex) { Alert($"保存失败: {ex.Message}", "错误"); }
    }

    private bool RunElevatedAction(string action)
    {
        if (Program.IsRunningAsAdmin())
        {
            try
            {
                var helper = new ServiceHelper();
                switch (action)
                {
                    case "--install": helper.InstallService(ServiceDir ?? _baseDir); break;
                    case "--uninstall": helper.UninstallService(); break;
                    case "--restart": helper.RestartService(); break;
                }
                return true;
            }
            catch (Exception ex) { Log($"操作失败: {ex.Message}", LogLevel.Error); return false; }
        }

        var arg = action == "--install" ? $"--install \"{ServiceDir ?? _baseDir}\"" : action;
        return Program.RelaunchAsAdmin(arg);
    }

    private void InstallService(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var confirm = owner == null
            ? MessageBox.Show("一键安装/重装 WebDAV 服务？", "确认", MessageBoxButton.YesNo)
            : MessageBox.Show(owner, "一键安装/重装 WebDAV 服务？", "确认", MessageBoxButton.YesNo);
        if (confirm != MessageBoxResult.Yes) return;

        var serviceDir = ServiceDir;
        if (serviceDir == null)
        {
            var searched = string.Join("\n", Path.Combine(AppContext.BaseDirectory, "service"),
                AppContext.BaseDirectory);
            Log("未找到 WebDAVService.exe，无法安装服务。", LogLevel.Error);
            Alert($"未找到 WebDAVService.exe。\n已查找：\n{searched}\n\n" +
                  "请确认发布时一并输出了 service 目录。", "错误");
            return;
        }

        SaveConfig(this, new RoutedEventArgs());
        Log("正在安装服务（会弹出 UAC 确认）...", LogLevel.Warn);
        if (!RunElevatedAction("--install")) { Log("提权失败（UAC 被取消）", LogLevel.Error); return; }

        var helper2 = new ServiceHelper();
        if (helper2.WaitForRunning(15000))
        {
            Log($"安装完成！服务已在端口 {_config.Port} 运行。", LogLevel.Success);
            Alert("服务安装完成！", "成功");
        }
        else
        {
            Log("服务已安装但未运行，请检查 logs/service.log", LogLevel.Warn);
        }
    }

    private void UninstallService(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var confirm = owner == null
            ? MessageBox.Show("确定要卸载 WebDAV 服务吗？", "确认", MessageBoxButton.YesNo)
            : MessageBox.Show(owner, "确定要卸载 WebDAV 服务吗？", "确认", MessageBoxButton.YesNo);
        if (confirm != MessageBoxResult.Yes) return;

        Log("正在卸载服务（会弹出 UAC 确认）...", LogLevel.Warn);
        if (RunElevatedAction("--uninstall")) Log("服务已卸载。", LogLevel.Success);
        else Log("卸载失败（UAC 被取消）", LogLevel.Error);
    }

    private void RestartService(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var confirm = owner == null
            ? MessageBox.Show("确定要重启 WebDAV 服务吗？", "确认", MessageBoxButton.YesNo)
            : MessageBox.Show(owner, "确定要重启 WebDAV 服务吗？", "确认", MessageBoxButton.YesNo);
        if (confirm != MessageBoxResult.Yes) return;

        Log("正在重启服务...", LogLevel.Warn);
        if (RunElevatedAction("--restart"))
        {
            var helper2 = new ServiceHelper();
            if (helper2.WaitForRunning(15000)) Log("服务已成功重启！", LogLevel.Success);
            else Log("服务状态异常。", LogLevel.Error);
        }
        else Log("重启失败（UAC 被取消）", LogLevel.Error);
    }

    // ==================== 连接二维码 ====================

    /// <summary>枚举本机可用的局域网 IPv4（跳过回环、APIPA 和未启用网卡）</summary>
    private void RefreshQrIps()
    {
        // 虚拟网卡（VMware/Hyper-V 等）往往没有默认网关，手机访问不到；
        // 按「有网关 > 以太网/WLAN > 其他」排序，默认选中第一项。
        var found = new List<(string Ip, string Nic, bool HasGateway, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));
                var rank = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet => 0,
                    NetworkInterfaceType.Wireless80211 => 1,
                    _ => 2,
                };
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    if (found.Any(f => f.Ip == ip)) continue;
                    found.Add((ip, nic.Name, hasGateway, rank));
                }
            }
        }
        catch { }

        var ordered = found
            .OrderByDescending(f => f.HasGateway)
            .ThenBy(f => f.Rank)
            .ThenBy(f => f.Nic)
            .ToList();

        var previous = GetQrIp();
        QrIpSelector.Items.Clear();
        foreach (var f in ordered)
            QrIpSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{f.Ip}  ·  {f.Nic}",
                Tag = f.Ip,
            });
        if (ordered.Count > 0)
        {
            var restore = (QrIpSelector.Items.Cast<ComboBoxItem>())
                .FirstOrDefault(i => (string?)i.Tag == previous);
            QrIpSelector.SelectedItem = (object?)restore ?? QrIpSelector.Items[0];
        }
    }

    /// <summary>下拉框当前选中的 IP（选项文本里带网卡名，真正取值走 Tag）</summary>
    private string GetQrIp() => QrIpSelector.SelectedItem switch
    {
        ComboBoxItem { Tag: string ip } => ip,
        string ip => ip,
        _ => "",
    };

    /// <summary>取用户列表当前选中的用户；没有选中时退回第一个</summary>
    private UserInfo? GetQrUser()
    {
        var text = LstUsers.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(text))
        {
            var name = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
            var selected = _config.Users.FirstOrDefault(u => u.Username == name);
            if (selected != null) return selected;
        }
        return _config.Users.FirstOrDefault();
    }

    private void GenerateQr(object sender, RoutedEventArgs e)
    {
        if (!new ServiceHelper().IsServiceRunning())
        {
            Log("服务未运行，无法生成连接二维码。请先安装/启动服务。", LogLevel.Warn);
            Alert("服务未运行，扫码也连不上。\n请先安装并启动服务后再生成二维码。");
            return;
        }

        if (ChkHttps.IsChecked != true)
        {
            Log("二维码必须使用 HTTPS：请勾选「启用 HTTPS（自签名证书）」并保存配置、重启服务。", LogLevel.Warn);
            Alert("二维码必须使用 HTTPS。\n请先勾选「启用 HTTPS（自签名证书）」，保存配置并重启服务。");
            return;
        }

        var ip = GetQrIp();
        if (string.IsNullOrWhiteSpace(ip))
        {
            RefreshQrIps();
            ip = GetQrIp();
            if (ip.Length == 0)
            {
                Log("没有找到可用的局域网 IPv4 地址。", LogLevel.Error);
                Alert("没有找到可用的局域网 IPv4 地址。");
                return;
            }
        }

        var user = GetQrUser();
        if (user == null)
        {
            Log("用户列表为空，无法生成二维码。", LogLevel.Warn);
            Alert("请先添加一个用户。");
            return;
        }
        if (string.IsNullOrEmpty(user.Password))
        {
            Log($"用户 {user.Username} 没有密码，二维码无法连接。", LogLevel.Warn);
            Alert("选中的用户没有密码，无法生成可连接的二维码。");
            return;
        }

        var port = int.TryParse(TxtHttpsPort.Text, out var httpsPort) ? httpsPort : _config.HttpsPort;
        var url = $"https://{ip}:{port}/";
        var payload = JsonSerializer.Serialize(new
        {
            v = 1,
            type = "webdav",
            name = Environment.MachineName,
            url,
            user = user.Username,
            pass = user.Password,
        });

        ShowQrWindow(payload, $"{user.Username} @ {url}");
        Log($"二维码已生成：{user.Username} @ {url}（HTTPS，含账号密码）", LogLevel.Success);
    }

    private void ShowQrWindow(string payload, string caption)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

        var workH = SystemParameters.WorkArea.Height;
        var px = (int)Math.Min(700, workH - 200);
        if (px < 400) px = 400;

        _qrWindow?.Close();

        var img = new System.Windows.Controls.Image
        {
            Source = RenderQr(data, px),
            Stretch = Stretch.Uniform,
            Width = px,
            Height = px,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

        var panel = new StackPanel { Background = Brushes.White };
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.Black,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 2),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "用 PocketNAS 扫码添加 · 含账号密码，关闭即销毁，请勿截屏分享",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.DimGray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var win = new Window
        {
            Title = "PocketNAS 扫码导入（关闭即销毁）",
            Content = panel,
            Width = px + 60,
            Height = px + 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = Brushes.White,
            ResizeMode = ResizeMode.NoResize,
        };
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_qrWindow, win)) _qrWindow = null;
        };
        _qrWindow = win;
        win.Show();
    }

    private static BitmapImage RenderQr(QRCodeData data, int targetPx)
    {
        var ppm = Math.Max(4, targetPx / data.ModuleMatrix.Count);
        var png = new PngByteQRCode(data).GetGraphic(ppm);
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }
}
