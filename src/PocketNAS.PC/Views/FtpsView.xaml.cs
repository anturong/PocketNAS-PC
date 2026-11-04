using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FtpsServer;
using PocketNAS.Shared;

namespace PocketNAS.PC.Views;

/// <summary>
/// FTPS 模块界面：与 WebDavView 同一套版式和交互逻辑。
/// 配置写在服务目录（service\ftps）下，与服务端读取位置一致。
/// </summary>
public partial class FtpsView : UserControl
{
    private FtpsOptions _options = new();
    private string _configPath = "";

    public FtpsView()
    {
        InitializeComponent();
        LoadConfig();
    }

    private static void Log(string message, LogLevel level = LogLevel.Info)
        => LogService.Write("FTPS", message, level);

    private void Alert(string message, string title = "提示")
        => MessageBox.Show(Window.GetWindow(this)!, message, title);

    /// <summary>服务目录存在 FtpsService.exe 时用它，否则用程序目录</summary>
    private static string ResolveConfigDir()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        var serviceDir = Path.Combine(baseDir, "service", "ftps");
        return File.Exists(Path.Combine(serviceDir, "FtpsService.exe")) ? serviceDir : baseDir;
    }

    private string ServiceDir => Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;

    // ==================== 配置 ====================

    private void LoadConfig()
    {
        _configPath = Path.Combine(ResolveConfigDir(), "ftps_config.json");
        _options = FtpsOptions.Load(_configPath);

        FillListenIps();
        TxtExplicitPort.Text = _options.ExplicitPort.ToString();
        TxtImplicitPort.Text = _options.ImplicitPort.ToString();
        TxtRootDir.Text = _options.ResolvedRootDir();
        TxtPasvMin.Text = _options.PasvMin.ToString();
        TxtPasvMax.Text = _options.PasvMax.ToString();
        ChkRequireTls.IsChecked = _options.RequireTls;

        RefreshUserList();
        if (LstUsers.Items.Count > 0 && LstUsers.SelectedIndex < 0)
            LstUsers.SelectedIndex = 0;
        UpdateLinks();

        Log("配置加载成功");
        Log($"配置文件：{_configPath}");
    }

    private void FillListenIps()
    {
        CmbListenIp.Items.Clear();
        CmbListenIp.Items.Add("0.0.0.0（全部网卡）");
        foreach (var address in LanAddresses.Get())
            CmbListenIp.Items.Add(address.ToString());

        var current = _options.ListenIp is "0.0.0.0" or "+" or "*"
            ? "0.0.0.0（全部网卡）"
            : _options.ListenIp;
        CmbListenIp.SelectedItem = current;
        if (CmbListenIp.SelectedIndex < 0) CmbListenIp.SelectedIndex = 0;
    }

    private string SelectedListenIp()
    {
        var text = CmbListenIp.SelectedItem?.ToString() ?? "0.0.0.0（全部网卡）";
        return text.StartsWith("0.0.0.0") ? "0.0.0.0" : text.Split("  ·  ")[0];
    }

    private void UpdateLinks()
    {
        var host = SelectedListenIp() == "0.0.0.0"
            ? (LanAddresses.Get().FirstOrDefault()?.Ip ?? "127.0.0.1")
            : SelectedListenIp();
        LnkExplicit.Text = $"ftps://{host}:{_options.ExplicitPort}/";
        LnkImplicit.Text = $"ftps://{host}:{_options.ImplicitPort}/";
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtRootDir.Text = dlg.SelectedPath;
    }

    private void OpenLink(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            try { Process.Start(new ProcessStartInfo(tb.Text) { UseShellExecute = true }); }
            catch (Exception ex) { Log($"打开失败: {ex.Message}", LogLevel.Error); }
        }
    }

    private void SaveConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            _options.ListenIp = SelectedListenIp();
            _options.ExplicitPort = int.Parse(TxtExplicitPort.Text);
            _options.ImplicitPort = int.Parse(TxtImplicitPort.Text);
            _options.RootDir = TxtRootDir.Text.Trim();
            _options.PasvMin = int.Parse(TxtPasvMin.Text);
            _options.PasvMax = int.Parse(TxtPasvMax.Text);
            _options.RequireTls = ChkRequireTls.IsChecked == true;

            if (_options.PasvMax < _options.PasvMin)
            {
                Alert("被动端口段的结束值不能小于起始值。");
                return;
            }

            _options.NormalizeRootDir();
            _options.Save();
            TxtRootDir.Text = _options.ResolvedRootDir();
            UpdateLinks();
            Log("配置已保存，重启服务后生效", LogLevel.Success);
        }
        catch (Exception ex)
        {
            Alert($"保存失败: {ex.Message}", "错误");
        }
    }

    // ==================== 用户管理 ====================

    private string PasswordValue => PwdPass.Visibility == Visibility.Visible ? PwdPass.Password : TxtPassPlain.Text;

    private void SetPassword(string value)
    {
        PwdPass.Password = value;
        TxtPassPlain.Text = value;
    }

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

        var icon = wasMasked ? Visibility.Collapsed : Visibility.Visible;
        EyeOpen.Visibility = icon;
        EyePupil.Visibility = icon;
        EyeSlash.Visibility = wasMasked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GeneratePassword(object sender, RoutedEventArgs e)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const int length = 16;

        var pool = upper + lower + digits + symbols;
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits), Pick(symbols) };
        while (chars.Count < length)
            chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        SetPassword(new string(chars.ToArray()));
        if (PwdPass.Visibility == Visibility.Visible) TogglePassVisibility(this, new RoutedEventArgs());
        Log($"已生成 {length} 位强密码");
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    private void RefreshUserList()
    {
        LstUsers.Items.Clear();
        foreach (var u in _options.Users)
            LstUsers.Items.Add($"{u.Username} [{(u.ReadOnly ? "只读" : "读写")}]");
    }

    private void OnUserSelect(object sender, SelectionChangedEventArgs e)
    {
        if (LstUsers.SelectedIndex < 0) return;
        var text = LstUsers.SelectedItem?.ToString() ?? "";
        var username = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
        var user = _options.Users.FirstOrDefault(u => u.Username == username);
        if (user == null) return;

        TxtUser.Text = user.Username;
        SetPassword(user.Password);
        ChkReadOnly.IsChecked = user.ReadOnly;
    }

    private void AddUser(object sender, RoutedEventArgs e)
    {
        var username = TxtUser.Text.Trim();
        var password = PasswordValue;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        { Alert("用户名和密码不能为空！"); return; }
        if (_options.Users.Any(u => u.Username == username))
        { Alert("用户已存在！"); return; }

        _options.Users.Add(new FtpsUser { Username = username, Password = password, ReadOnly = ChkReadOnly.IsChecked == true });
        RefreshUserList();
        TxtUser.Clear();
        SetPassword("");
        ChkReadOnly.IsChecked = false;
        Log($"用户 {username} 已添加（未保存）", LogLevel.Warn);
    }

    private void UpdateUser(object sender, RoutedEventArgs e)
    {
        if (LstUsers.SelectedIndex < 0) { Alert("请先选择一个用户。"); return; }
        var text = LstUsers.SelectedItem?.ToString() ?? "";
        var oldName = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
        var password = PasswordValue;
        if (string.IsNullOrEmpty(password)) { Alert("密码不能为空！"); return; }

        var user = _options.Users.FirstOrDefault(u => u.Username == oldName);
        if (user == null) return;

        user.Password = password;
        user.ReadOnly = ChkReadOnly.IsChecked == true;
        RefreshUserList();
        Log($"用户 {oldName} 信息已更新（未保存）", LogLevel.Warn);
    }

    private void DeleteUser(object sender, RoutedEventArgs e)
    {
        if (LstUsers.SelectedIndex < 0) return;
        var text = LstUsers.SelectedItem?.ToString() ?? "";
        var username = text.Contains('[') ? text.Split('[')[0].Trim() : text.Trim();
        if (MessageBox.Show(Window.GetWindow(this)!, $"确定要删除用户 {username} 吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        _options.Users.RemoveAll(u => u.Username == username);
        RefreshUserList();
        Log($"用户 {username} 已删除（未保存）", LogLevel.Warn);
    }

    // ==================== 服务控制 ====================

    private bool RunElevated(string argument)
    {
        if (Elevation.IsRunningAsAdmin())
        {
            try
            {
                var helper = new FtpsServiceHelper();
                switch (argument)
                {
                    case "--ftps-install": helper.InstallService(ServiceDir); break;
                    case "--ftps-uninstall": helper.UninstallService(ServiceDir); break;
                    case "--ftps-restart": helper.RestartService(); break;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"操作失败: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        var args = argument == "--ftps-install" ? $"{argument} \"{ServiceDir}\"" : argument;
        return Elevation.RelaunchAsAdmin(args);
    }

    private void InstallService(object sender, RoutedEventArgs e)
    {
        SaveConfig(this, new RoutedEventArgs());
        var exePath = Path.Combine(ServiceDir, "FtpsService.exe");
        if (!File.Exists(exePath))
        {
            Alert($"未找到 FtpsService.exe:\n{exePath}\n\n请先构建（服务会随构建发布到 service\\ftps）。", "错误");
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!, "一键安装/重装 FTPS 服务？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        Log("正在安装 FTPS 服务（会弹出 UAC 确认）...", LogLevel.Warn);
        if (!RunElevated("--ftps-install"))
        {
            Log("提权失败（UAC 被取消）", LogLevel.Error);
            return;
        }

        if (new FtpsServiceHelper().WaitForRunning(15000))
        {
            Log("安装完成，服务已运行", LogLevel.Success);
            Alert("服务安装完成！", "成功");
        }
        else
        {
            Log("服务已安装但未运行，请检查 service\\ftps\\logs\\service.log", LogLevel.Warn);
        }
    }

    private void UninstallService(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "确定要卸载 FTPS 服务吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        Log("正在卸载 FTPS 服务（会弹出 UAC 确认）...", LogLevel.Warn);
        if (RunElevated("--ftps-uninstall")) Log("服务已卸载", LogLevel.Success);
        else Log("卸载失败（UAC 被取消）", LogLevel.Error);
    }

    private void RestartService(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "确定要重启 FTPS 服务吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        Log("正在重启 FTPS 服务...", LogLevel.Warn);
        if (RunElevated("--ftps-restart"))
        {
            if (new FtpsServiceHelper().WaitForRunning(15000)) Log("服务已重启", LogLevel.Success);
            else Log("服务状态异常，请检查日志", LogLevel.Error);
        }
        else Log("重启失败（UAC 被取消）", LogLevel.Error);
    }
}
