using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace WebDAVConfigurator
{
    public partial class MainWindow : Window
    {
        private const int MaxLogLines = 500;

        private readonly string _baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        private WebDAVConfigData _config = null!;

        public string AppTitle => $"Easy WebDAV v{AppInfo.Version} - 服务管理器";

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadConfig();
        }

        // ==================== Logging ====================

        private void Log(string message, string color = "Gray")
        {
            Dispatcher.Invoke(() =>
            {
                var run = new Run($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                run.Foreground = color switch
                {
                    "Green" => System.Windows.Media.Brushes.LightGreen,
                    "Yellow" => System.Windows.Media.Brushes.Yellow,
                    "Red" => System.Windows.Media.Brushes.LightPink,
                    "Cyan" => System.Windows.Media.Brushes.Cyan,
                    _ => System.Windows.Media.Brushes.LightGray
                };
                var paragraph = new Paragraph(run) { Margin = new Thickness(0) };
                LogConsole.Document.Blocks.Add(paragraph);

                while (LogConsole.Document.Blocks.Count > MaxLogLines)
                    LogConsole.Document.Blocks.Remove(LogConsole.Document.Blocks.FirstBlock);

                LogConsole.ScrollToEnd();
            });
        }

        // ==================== Config ====================

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
            Log("配置加载成功。", "Cyan");
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
                    Log($"打开浏览器失败: {ex.Message}", "Red");
                }
            }
        }

        // ==================== User Management ====================

        private void OnUserSelect(object sender, SelectionChangedEventArgs e)
        {
            if (LstUsers.SelectedIndex < 0) return;
            var text = LstUsers.SelectedItem?.ToString() ?? "";
            var username = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
            var user = _config.Users.FirstOrDefault(u => u.Username == username);
            if (user != null)
            {
                TxtUser.Text = user.Username;
                TxtPass.Text = user.Password;
                ChkReadOnly.IsChecked = user.ReadOnly;
            }
        }

        private void AddUser(object sender, RoutedEventArgs e)
        {
            var username = TxtUser.Text.Trim();
            var password = TxtPass.Text;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            { System.Windows.MessageBox.Show("用户名和密码不能为空！"); return; }
            if (_config.Users.Any(u => u.Username == username))
            { System.Windows.MessageBox.Show("用户已存在！"); return; }

            _config.Users.Add(new UserInfo { Username = username, Password = password, ReadOnly = ChkReadOnly.IsChecked == true });
            RefreshUserList();
            TxtUser.Clear(); TxtPass.Clear(); ChkReadOnly.IsChecked = false;
            Log($"用户 {username} 已添加 (未保存)", "Yellow");
        }

        private void UpdateUser(object sender, RoutedEventArgs e)
        {
            if (LstUsers.SelectedIndex < 0) { System.Windows.MessageBox.Show("请先选择一个用户。"); return; }
            var text = LstUsers.SelectedItem?.ToString() ?? "";
            var oldName = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
            var password = TxtPass.Text;
            if (string.IsNullOrEmpty(password)) { System.Windows.MessageBox.Show("密码不能为空！"); return; }

            var user = _config.Users.FirstOrDefault(u => u.Username == oldName);
            if (user != null)
            {
                user.Password = password;
                user.ReadOnly = ChkReadOnly.IsChecked == true;
                RefreshUserList();
                Log($"用户 {oldName} 信息已更新 (未保存)", "Yellow");
            }
        }

        private void DeleteUser(object sender, RoutedEventArgs e)
        {
            if (LstUsers.SelectedIndex < 0) return;
            var text = LstUsers.SelectedItem?.ToString() ?? "";
            var username = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
            if (System.Windows.MessageBox.Show($"确定要删除用户 {username} 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _config.Users.RemoveAll(u => u.Username == username);
                RefreshUserList();
                Log($"用户 {username} 已删除 (未保存)", "Yellow");
            }
        }

        // ==================== Save & Service ====================

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
                    System.Windows.MessageBox.Show("至少启用 HTTP 或 HTTPS 其中之一。", "提示");
                    return;
                }

                _config.Save();
                UpdateServerLink();
                Log("配置已保存到文件。", "Green");

                if (Program.IsRunningAsAdmin())
                {
                    foreach (var (ok, msg) in FirewallHelper.EnsureRules(_config))
                        Log(msg, ok ? "Green" : "Red");
                }
                else
                {
                    var ports = new List<int>();
                    if (_config.EnableHttp) ports.Add(_config.Port);
                    if (_config.EnableHttps) ports.Add(_config.HttpsPort);
                    Log($"防火墙规则需管理员权限，安装服务时会自动放行端口 {string.Join("/", ports)}", "Yellow");
                }

                System.Windows.MessageBox.Show("配置已保存！\n请重启服务使配置生效。", "成功");
            }
            catch (Exception ex) { System.Windows.MessageBox.Show($"保存失败: {ex.Message}", "错误"); }
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
                        case "--install": helper.InstallService(_baseDir); break;
                        case "--uninstall": helper.UninstallService(); break;
                        case "--restart": helper.RestartService(); break;
                    }
                    return true;
                }
                catch (Exception ex) { Log($"操作失败: {ex.Message}", "Red"); return false; }
            }

            var arg = action == "--install" ? $"--install \"{_baseDir}\"" : action;
            return Program.RelaunchAsAdmin(arg);
        }

        private void InstallService(object sender, RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("一键安装/重装 WebDAV 服务？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            var exePath = Path.Combine(_baseDir, "WebDAVService.exe");
            if (!File.Exists(exePath)) { System.Windows.MessageBox.Show($"未找到 WebDAVService.exe: {exePath}", "错误"); return; }

            SaveConfig(this, new RoutedEventArgs());
            Log("正在安装服务（会弹出 UAC 确认）...", "Yellow");
            var success = RunElevatedAction("--install");
            if (!success) { Log("提权失败（UAC 被取消）", "Red"); return; }

            var helper2 = new ServiceHelper();
            if (helper2.WaitForRunning(15000))
            {
                Log($"安装完成！服务已在端口 {_config.Port} 运行。", "Green");
                System.Windows.MessageBox.Show("服务安装完成！", "成功");
            }
            else { Log("服务已安装但未运行，请检查 logs/service.log", "Yellow"); }
        }

        private void UninstallService(object sender, RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("确定要卸载 WebDAV 服务吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Log("正在卸载服务（会弹出 UAC 确认）...", "Yellow");
            if (RunElevatedAction("--uninstall")) Log("服务已卸载。", "Green");
            else Log("卸载失败（UAC 被取消）", "Red");
        }

        private void RestartService(object sender, RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("确定要重启 WebDAV 服务吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Log("正在重启服务...", "Yellow");
            if (RunElevatedAction("--restart"))
            {
                var helper2 = new ServiceHelper();
                if (helper2.WaitForRunning(15000)) Log("服务已成功重启！", "Green");
                else Log("服务状态异常。", "Red");
            }
            else Log("重启失败（UAC 被取消）", "Red");
        }

        // ==================== 连接二维码 ====================

        private Window? _qrWindow;

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
                QrIpSelector.SelectedItem = restore ?? QrIpSelector.Items[0];
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
                Log("服务未运行，无法生成连接二维码。请先安装/启动服务。", "Yellow");
                System.Windows.MessageBox.Show("服务未运行，扫码也连不上。\n请先安装并启动服务后再生成二维码。", "提示");
                return;
            }

            if (ChkHttps.IsChecked != true)
            {
                Log("二维码必须使用 HTTPS：请勾选「启用 HTTPS (自签名证书)」并保存配置、重启服务。", "Yellow");
                System.Windows.MessageBox.Show("二维码必须使用 HTTPS。\n请先勾选「启用 HTTPS (自签名证书)」，保存配置并重启服务。", "提示");
                return;
            }

            var ip = GetQrIp();
            if (string.IsNullOrWhiteSpace(ip))
            {
                RefreshQrIps();
                ip = GetQrIp();
                if (ip.Length == 0)
                {
                    Log("没有找到可用的局域网 IPv4 地址。", "Red");
                    System.Windows.MessageBox.Show("没有找到可用的局域网 IPv4 地址。", "提示");
                    return;
                }
            }

            var user = GetQrUser();
            if (user == null)
            {
                Log("用户列表为空，无法生成二维码。", "Yellow");
                System.Windows.MessageBox.Show("请先添加一个用户。", "提示");
                return;
            }
            if (string.IsNullOrEmpty(user.Password))
            {
                Log($"用户 {user.Username} 没有密码，二维码无法连接。", "Yellow");
                System.Windows.MessageBox.Show("选中的用户没有密码，无法生成可连接的二维码。", "提示");
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
            Log($"二维码已生成：{user.Username} @ {url}（HTTPS，含账号密码）", "Green");
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

            var panel = new StackPanel { Background = System.Windows.Media.Brushes.White };
            panel.Children.Add(img);
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Black,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 2),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "用 PocketNAS 扫码添加 · 含账号密码，关闭即销毁，请勿截屏分享",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.DimGray,
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
                Owner = this,
                Background = System.Windows.Media.Brushes.White,
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
}
