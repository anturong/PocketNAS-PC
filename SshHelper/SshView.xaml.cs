using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Text.Json;
using PocketNAS.Shared;
using QRCoder;

namespace SshHelper;

public partial class SshView : System.Windows.Controls.UserControl
{
    private enum LogColor { Cyan, Green, Yellow, Red, Default }
    private string PrivateKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
    private string PubKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519.pub");
    private string AuthKeysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");
    private string AuthKeysAdminPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys");
    private string BakDir = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "bak");

    public SshView()
    {
        InitializeComponent();
        TxtUser.Text = Environment.UserName;
        LoadLocalIPs();
    }

    // ==================== UI 交互逻辑 ====================
    // 如需更改 SSH 登录用户名：Win+R → netplwiz → 选中当前用户 → 属性 → 改用户名 → 重启
    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        BtnStart.IsEnabled = false;
        await Task.Run(() => RunFullConfiguration());
        BtnStart.IsEnabled = true;
    }

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        BtnTest.IsEnabled = false;
        await Task.Run(() => TestSshConnection());
        BtnTest.IsEnabled = true;
    }

    private void ToggleKey_Click(object sender, RoutedEventArgs e)
    {
        if (TxtPrivateKey.Visibility == Visibility.Collapsed)
        {
            if (File.Exists(PrivateKeyPath))
            {
                TxtPrivateKey.Text = File.ReadAllText(PrivateKeyPath);
                TxtPrivateKey.Visibility = Visibility.Visible;
            }
            else
            {
                AppendLog("私钥文件不存在，无法显示。\n", LogColor.Red);
            }
        }
        else
        {
            TxtPrivateKey.Visibility = Visibility.Collapsed;
        }
    }

    private void CopyKeyText_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(PrivateKeyPath))
        {
            if (Win32ClipboardSetText(File.ReadAllText(PrivateKeyPath)))
                MessageBox.Show("私钥文本已复制到剪贴板！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("复制失败：剪贴板被其他进程占用，请重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show("私钥文件不存在！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyKeyFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(PrivateKeyPath))
        {
            MessageBox.Show("私钥文件不存在！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (Win32ClipboardSetFileDropList(PrivateKeyPath))
            MessageBox.Show("私钥文件已复制到剪贴板，可在资源管理器中粘贴！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show("复制失败：剪贴板被其他进程占用，请重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void ReplaceKey_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "即将执行以下操作：\n\n" +
            "1. 备份现有密钥到 bak 文件夹\n" +
            "2. 生成新的 ED25519 密钥对\n" +
            "3. 部署新公钥（authorized_keys + administrators_authorized_keys）\n" +
            "4. 测试新密钥登录\n" +
            "5. 复制新私钥到剪贴板\n\n" +
            "⚠ 确认继续？",
            "更换密钥", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        BtnStart.IsEnabled = false;
        await Task.Run(() => RunReplaceKey());
        BtnStart.IsEnabled = true;
    }

    private void ShowUserHelp(object sender, MouseButtonEventArgs e)
    {
        MessageBox.Show(
            "SSH 登录用户名 = Windows 系统用户名\n\n" +
            "如需更改 SSH 登录用户名：\n" +
            "  Win+R → netplwiz → 回车\n" +
            "  选中当前用户 → 属性 → 改用户名 → 重启\n\n" +
            "当前用户名: " + Environment.UserName,
            "关于用户名", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyText(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            if (Win32ClipboardSetText(tb.Text))
                AppendLog($"已复制: {tb.Text}\n", LogColor.Cyan);
            else
                AppendLog("复制失败：剪贴板被其他进程占用\n", LogColor.Red);
        }
    }

    // ==================== 更换密钥逻辑 ====================
    private void RunReplaceKey()
    {
        try
        {
            AppendLog("=== 0/5 确保 OpenSSH Client 可用 ===\n", LogColor.Cyan);
            EnsureOpenSshClient();

            AppendLog("\n=== 1/5 备份现有密钥 ===\n", LogColor.Cyan);
            BackupKeys();

            AppendLog("\n=== 2/5 生成新密钥 ===\n", LogColor.Cyan);
            GenerateNewKeys();

            AppendLog("\n=== 3/5 部署新公钥 ===\n", LogColor.Cyan);
            DeployPublicKey();

            AppendLog("\n=== 4/5 验证新密钥 ===\n", LogColor.Cyan);
            TestSshConnection();

            AppendLog("\n=== 5/5 复制新私钥到剪贴板 ===\n", LogColor.Cyan);
            CopyNewPrivateKey();
        }
        catch (Exception ex)
        {
            AppendLog($"\n更换密钥失败：{ex.Message}\n", LogColor.Red);
        }
        finally
        {
            // 密钥已经换过（或可能换过），之前生成的二维码全部作废 —— 必须手动重新生成
            InvalidateQr("密钥已更换，旧二维码作废；确认 SSH 服务正常后请重新「生成二维码」");
        }
    }

    /// <summary>作废二维码：关掉弹窗、清掉内容、收起按钮（密钥轮换后调用；
    /// 可能跑在后台线程，UI 走 Dispatcher）</summary>
    private void InvalidateQr(string hint)
    {
        Dispatcher.Invoke(() =>
        {
            qrWindow?.Close();
            qrWindow = null;
            lastPayload = null;
            BtnQrAgain.Visibility = Visibility.Collapsed;
            TxtQrHint.Text = hint;
        });
        AppendLog($"\n{hint}\n", LogColor.Yellow);
    }

    private void BackupKeys()
    {
        var sshDir = Path.GetDirectoryName(PrivateKeyPath);
        if (sshDir is null || !Directory.Exists(sshDir))
        {
            AppendLog("~/.ssh 目录不存在，跳过备份\n", LogColor.Yellow);
            return;
        }

        if (!Directory.Exists(BakDir))
            Directory.CreateDirectory(BakDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipPath = Path.Combine(BakDir, $"{timestamp}.zip");

        ZipFile.CreateFromDirectory(sshDir, zipPath);
        AppendLog($"已备份到: {zipPath}\n", LogColor.Green);
    }

    private void GenerateNewKeys()
    {
        // 确保 ~/.ssh 目录存在，否则 ssh-keygen 会失败
        var sshDir = Path.GetDirectoryName(PrivateKeyPath);
        if (sshDir is not null && !Directory.Exists(sshDir))
        {
            Directory.CreateDirectory(sshDir);
            AppendLog($"已创建目录: {sshDir}\n", LogColor.Green);
        }

        // 清理旧密钥（仅在旧密钥存在时才删除）
        if (File.Exists(PrivateKeyPath))
        {
            AppendLog($"删除旧私钥: {PrivateKeyPath}\n", LogColor.Yellow);
            File.Delete(PrivateKeyPath);
        }
        if (File.Exists(PubKeyPath))
        {
            AppendLog($"删除旧公钥: {PubKeyPath}\n", LogColor.Yellow);
            File.Delete(PubKeyPath);
        }

        // 尝试多个可能的 ssh-keygen 路径
        string sshKeygenPath = FindSshKeygen();
        AppendLog($"使用 ssh-keygen: {sshKeygenPath}\n", LogColor.Cyan);

        // 为新密钥文件预设正确的 ACL（继承 .ssh 目录的权限）
        var psi = new ProcessStartInfo
        {
            FileName = sshKeygenPath,
            Arguments = $"-t ed25519 -f \"{PrivateKeyPath}\" -N \"\" -q",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppendLog($"启动 ssh-keygen 失败（路径: {sshKeygenPath}）: {ex.Message}\n", LogColor.Red);
            AppendLog("→ 请确认 OpenSSH Client 已安装：Get-WindowsCapability -Online | ? Name -like '*OpenSSH.Client*'\n", LogColor.Cyan);
            return;
        }
        if (process is null)
        {
            AppendLog($"无法启动 ssh-keygen（尝试路径: {sshKeygenPath}），请确保已安装 OpenSSH Client\n", LogColor.Red);
            return;
        }
        var err = process.StandardError.ReadToEnd();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);
        if (process.ExitCode != 0)
        {
            AppendLog($"密钥生成失败（退出码 {process.ExitCode}）\n", LogColor.Red);
            if (!string.IsNullOrWhiteSpace(err))
                AppendLog($"  错误输出: {err.Trim()}\n", LogColor.Red);
            if (!string.IsNullOrWhiteSpace(output))
                AppendLog($"  标准输出: {output.Trim()}\n", LogColor.Yellow);
            return;
        }

        // 验证文件确实生成了（某些情况下 ssh-keygen 退出码 0 但实际未写入）
        if (!File.Exists(PrivateKeyPath) || !File.Exists(PubKeyPath))
        {
            AppendLog("密钥生成异常：ssh-keygen 正常退出但密钥文件未生成\n", LogColor.Red);
            AppendLog($"  期望私钥: {PrivateKeyPath}\n", LogColor.Yellow);
            AppendLog($"  期望公钥: {PubKeyPath}\n", LogColor.Yellow);
            // 检查输出，可能有提示信息
            if (!string.IsNullOrWhiteSpace(output))
                AppendLog($"  ssh-keygen 输出: {output.Trim()}\n", LogColor.Yellow);
            return;
        }
        AppendLog("新 ED25519 密钥对已生成\n", LogColor.Green);
    }

    /// <summary>
    /// 查找 ssh-keygen.exe，依次尝试：System32\OpenSSH、ProgramFiles\OpenSSH、ProgramFiles(x86)\OpenSSH、
    /// Git for Windows\usr\bin、PATH 环境变量中的目录，最后回退到裸 "ssh-keygen" 由 OS 通过 PATH 解析。
    /// </summary>
    private static string FindSshKeygen()
    {
        string[] candidates =
        {
            Path.Combine(Environment.SystemDirectory, @"OpenSSH\ssh-keygen.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenSSH\ssh-keygen.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"OpenSSH\ssh-keygen.exe"),
            // Git for Windows / MSYS2 可能自带 OpenSSH
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\usr\bin\ssh-keygen.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\usr\bin\ssh-keygen.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        // 在 PATH 环境变量中搜索
        var foundOnPath = FindExeOnPath("ssh-keygen.exe");
        if (foundOnPath is not null) return foundOnPath;
        // 最终回退：裸名称，依赖系统 PATH（适用于用户自定安装位置）
        return "ssh-keygen";
    }

    /// <summary>
    /// 查找 ssh.exe，与 FindSshKeygen 同样的搜索策略。
    /// </summary>
    private static string FindSsh()
    {
        string[] candidates =
        {
            Path.Combine(Environment.SystemDirectory, @"OpenSSH\ssh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenSSH\ssh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"OpenSSH\ssh.exe"),
            // Git for Windows / MSYS2 可能自带 OpenSSH
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\usr\bin\ssh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\usr\bin\ssh.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        var foundOnPath = FindExeOnPath("ssh.exe");
        if (foundOnPath is not null) return foundOnPath;
        return "ssh";
    }

    /// <summary>
    /// 在 PATH 环境变量的各个目录中搜索指定可执行文件。
    /// </summary>
    private static string? FindExeOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(fullPath)) return fullPath;
        }
        return null;
    }

    private void DeployPublicKey()
    {
        if (!File.Exists(PubKeyPath))
        {
            AppendLog("公钥文件不存在，无法部署\n", LogColor.Red);
            return;
        }

        string pubKey = File.ReadAllText(PubKeyPath).Trim();

        // 写入 ~/.ssh/authorized_keys
        var sshDir = Path.GetDirectoryName(AuthKeysPath);
        if (sshDir is not null && !Directory.Exists(sshDir)) Directory.CreateDirectory(sshDir);
        TryGrantWriteAccess(AuthKeysPath);
        try
        {
            File.WriteAllText(AuthKeysPath, pubKey + Environment.NewLine);
            AppendLog("公钥已写入 authorized_keys\n", LogColor.Green);
        }
        catch (Exception ex)
        {
            AppendLog($"写入 authorized_keys 失败：{ex.Message}\n", LogColor.Red);
        }

        // 写入 C:\ProgramData\ssh\administrators_authorized_keys
        var adminSshDir = Path.GetDirectoryName(AuthKeysAdminPath);
        if (adminSshDir is not null && !Directory.Exists(adminSshDir))
        {
            try { Directory.CreateDirectory(adminSshDir); }
            catch (Exception ex)
            {
                AppendLog($"无法创建 {adminSshDir}（可能需要管理员权限）：{ex.Message}\n", LogColor.Yellow);
                // 不阻断流程 — authorized_keys 可能已足够
            }
        }
        TryGrantWriteAccess(AuthKeysAdminPath);
        try
        {
            File.WriteAllText(AuthKeysAdminPath, pubKey + Environment.NewLine);
            AppendLog("公钥已写入 administrators_authorized_keys\n", LogColor.Green);
        }
        catch (Exception ex)
        {
            AppendLog($"写入 administrators_authorized_keys 失败（可能需要管理员权限）：{ex.Message}\n", LogColor.Yellow);
            AppendLog("→ 如果你的 sshd_config 未配置 administrators_authorized_keys，可忽略此项\n", LogColor.Cyan);
        }

        // 设置权限
        TrySetAuthorizedKeysPermission();
        TrySetAdminAuthorizedKeysPermission();
    }

    // 为当前用户添加写权限，用于覆盖之前设的只读 ACL
    private void TryGrantWriteAccess(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.Write,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            AppendLog($"ACL 写权限设置失败（{path}）：{ex.Message}\n", LogColor.Yellow);
        }
    }

    // 复制新私钥到剪贴板（仅在更换密钥流程中调用）
    private void CopyNewPrivateKey()
    {
        if (!File.Exists(PrivateKeyPath))
        {
            AppendLog("私钥文件不存在，无法复制\n", LogColor.Red);
            return;
        }

        Dispatcher.Invoke(() =>
        {
            var text = File.ReadAllText(PrivateKeyPath);
            if (Win32ClipboardSetText(text))
            {
                AppendLog("新私钥已复制到剪贴板，请妥善保管！\n", LogColor.Green);
            }
            else
            {
                AppendLog("复制到剪贴板失败：剪贴板被其他进程占用\n", LogColor.Red);
                AppendLog("新私钥保存在: " + PrivateKeyPath + "\n", LogColor.Yellow);
            }
        });
    }

    // 用 Win32 API 直接设置剪贴板文本，比 WPF Clipboard.SetText 更可靠
    private static bool Win32ClipboardSetText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    IntPtr ptr = Marshal.StringToHGlobalUni(text);
                    if (SetClipboardData(13, ptr) == IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(ptr);
                        return false;
                    }
                    return true; // 剪贴板接管了内存，不要 Free
                }
                finally
                {
                    CloseClipboard();
                }
            }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int pt_x;
        public int pt_y;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fNC;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fWide;
    }

    private static bool Win32ClipboardSetFileDropList(string filePath)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();

                    byte[] files = Encoding.Unicode.GetBytes(filePath + "\0\0");
                    var dropfiles = new DROPFILES
                    {
                        pFiles = (uint)Marshal.SizeOf<DROPFILES>(),
                        fWide = true
                    };
                    int structSize = Marshal.SizeOf<DROPFILES>();
                    byte[] structBytes = new byte[structSize];
                    IntPtr p = Marshal.AllocHGlobal(structSize);
                    try
                    {
                        Marshal.StructureToPtr(dropfiles, p, false);
                        Marshal.Copy(p, structBytes, 0, structSize);

                        byte[] data = new byte[structSize + files.Length];
                        Buffer.BlockCopy(structBytes, 0, data, 0, structSize);
                        Buffer.BlockCopy(files, 0, data, structSize, files.Length);

                        IntPtr hGlobal = Marshal.AllocHGlobal(data.Length);
                        Marshal.Copy(data, 0, hGlobal, data.Length);

                        if (SetClipboardData(0xF, hGlobal) == IntPtr.Zero) // 0xF = CF_HDROP
                        {
                            Marshal.FreeHGlobal(hGlobal);
                            return false;
                        }
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(p);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    private void TrySetAdminAuthorizedKeysPermission()
    {
        if (!File.Exists(AuthKeysAdminPath)) return;
        try
        {
            var fileInfo = new FileInfo(AuthKeysAdminPath);
            var security = fileInfo.GetAccessControl();

            // 禁用继承，清除所有继承权限
            security.SetAccessRuleProtection(true, false);

            // SYSTEM 读取权限（通过 SID 名称，跨语言通用）
            security.SetAccessRule(new FileSystemAccessRule("SYSTEM",
                FileSystemRights.Read, AccessControlType.Allow));

            // BUILTIN\Administrators 读取权限（NT 权威 SID，跨语言通用）
            security.SetAccessRule(new FileSystemAccessRule("BUILTIN\\Administrators",
                FileSystemRights.Read, AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
            AppendLog("administrators_authorized_keys ACL 权限设置完成\n", LogColor.Green);
        }
        catch (Exception ex)
        {
            AppendLog($"administrators_authorized_keys ACL 设置失败（可能需要管理员权限）：{ex.Message}\n", LogColor.Yellow);
        }
    }

    // ==================== 核心配置逻辑 ====================
    private void RunFullConfiguration()
    {
        try
        {
            // 1. 检查并安装 OpenSSH
            AppendLog("=== 检查 OpenSSH 组件 ===\n", LogColor.Cyan);
            RunPowershellCommand("Get-WindowsCapability -Online | Where-Object { $_.Name -like 'OpenSSH.Client*' -and $_.State -ne 'Installed' } | Add-WindowsCapability -Online", "安装 OpenSSH Client...");
            RunPowershellCommand("Get-WindowsCapability -Online | Where-Object { $_.Name -like 'OpenSSH.Server*' -and $_.State -ne 'Installed' } | Add-WindowsCapability -Online", "安装 OpenSSH Server...");

            // 2. 检查 sshd 服务
            AppendLog("\n=== 检查 sshd 服务状态 ===\n", LogColor.Cyan);
            try
            {
                var sshd = new ServiceController("sshd");
                if (sshd.Status != ServiceControllerStatus.Running)
                {
                    AppendLog("设置 sshd 为自动启动...\n", LogColor.Yellow);
                    RunPowershellCommand("sc.exe config sshd start= auto", "sshd 已设为自动启动");
                    AppendLog("启动 sshd 服务...\n", LogColor.Yellow);
                    sshd.Start();
                    sshd.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
                AppendLog("sshd 服务运行正常\n", LogColor.Green);
                AppendLog("确保 sshd 启动类型为自动...\n", LogColor.Yellow);
                RunPowershellCommand("sc.exe config sshd start= auto", "sshd 启动类型已确认");
            }
            catch (InvalidOperationException)
            {
                AppendLog("sshd 服务未安装，OpenSSH Server 安装可能失败\n", LogColor.Red);
                AppendLog("→ 请手动以管理员身份运行：Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0\n", LogColor.Cyan);
            }

            // 2b. 检查 sshd_config 中 Administrators 组的重定向设置
            AppendLog("\n=== 检查 sshd_config 配置 ===\n", LogColor.Cyan);
            var sshdConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH", "sshd_config");
            if (!File.Exists(sshdConfigPath))
                sshdConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "sshd_config");
            if (File.Exists(sshdConfigPath))
            {
                var config = File.ReadAllText(sshdConfigPath);
                if (config.Contains("Match Group Administrators") || config.Contains("administrators_authorized_keys"))
                {
                    AppendLog("检测到 sshd_config 为 Administrators 组配置了专用授权文件\n", LogColor.Yellow);
                    AppendLog("→ 程序将同步写公钥到 administrators_authorized_keys\n", LogColor.Cyan);
                }
            }

            // 3. 检查 / 自动生成密钥
            AppendLog("\n=== 检查密钥文件 ===\n", LogColor.Cyan);
            if (!File.Exists(PrivateKeyPath))
            {
                AppendLog("未找到私钥，自动生成 ED25519 密钥对...\n", LogColor.Yellow);
                GenerateNewKeys();
                if (!File.Exists(PrivateKeyPath))
                {
                    AppendLog("密钥生成失败，请手动运行：ssh-keygen -t ed25519\n", LogColor.Red);
                    return;
                }
            }

            // 4. 配置 authorized_keys + administrators_authorized_keys
            AppendLog("\n=== 配置公钥授权 ===\n", LogColor.Cyan);
            if (File.Exists(PubKeyPath))
            {
                string pubKey = File.ReadAllText(PubKeyPath).Trim();

                // 4a. ~/.ssh/authorized_keys
                string? sshDir = Path.GetDirectoryName(AuthKeysPath);
                if (sshDir is not null && !Directory.Exists(sshDir)) Directory.CreateDirectory(sshDir);

                bool keyExists = false;
                if (File.Exists(AuthKeysPath))
                {
                    keyExists = File.ReadAllLines(AuthKeysPath).Any(line => line.Trim() == pubKey);
                }

                if (!keyExists)
                {
                    File.AppendAllText(AuthKeysPath, pubKey + Environment.NewLine);
                    AppendLog("公钥已添加到 authorized_keys\n", LogColor.Green);
                }
                else
                {
                    AppendLog("公钥已存在 authorized_keys，跳过\n", LogColor.Yellow);
                }

                // 4b. C:\ProgramData\ssh\administrators_authorized_keys
                string? adminSshDir = Path.GetDirectoryName(AuthKeysAdminPath);
                if (adminSshDir is not null && !Directory.Exists(adminSshDir)) Directory.CreateDirectory(adminSshDir);

                bool adminKeyExists = false;
                if (File.Exists(AuthKeysAdminPath))
                {
                    adminKeyExists = File.ReadAllLines(AuthKeysAdminPath).Any(line => line.Trim() == pubKey);
                }

                if (!adminKeyExists)
                {
                    File.WriteAllText(AuthKeysAdminPath, pubKey + Environment.NewLine);
                    AppendLog("公钥已写入 administrators_authorized_keys\n", LogColor.Green);
                }
                else
                {
                    AppendLog("公钥已存在 administrators_authorized_keys，跳过\n", LogColor.Yellow);
                }
            }
            else
            {
                AppendLog("警告：找不到公钥文件，跳过授权配置\n", LogColor.Yellow);
            }

            // 5. 设置权限
            AppendLog("\n=== 设置文件权限 ===\n", LogColor.Cyan);
            TrySetAuthorizedKeysPermission();
            TrySetAdminAuthorizedKeysPermission();

            // 6. 测试连接
            AppendLog("\n=== 测试本机密钥登录 ===\n", LogColor.Cyan);
            TestSshConnection();

            AppendLog("\n配置完成：现在可以点右上「生成二维码」，用 PocketNAS 扫码导入私钥连接。\n", LogColor.Green);
        }
        catch (Exception ex)
        {
            AppendLog($"\n执行出错：{ex.Message}\n", LogColor.Red);
        }
    }

    private void TestSshConnection()
    {
        var sshUser = Environment.UserName;
        AppendLog($"正在测试 {sshUser}@127.0.0.1 ...\n", LogColor.Yellow);
        try
        {
            var sshPath = FindSsh();
            var psi = new ProcessStartInfo
            {
                FileName = sshPath,
                Arguments = $"-o StrictHostKeyChecking=accept-new -o ConnectTimeout=5 -o BatchMode=yes -i \"{PrivateKeyPath}\" {Environment.UserName}@127.0.0.1 exit",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppendLog($"无法启动 ssh（路径: {sshPath}）: {ex.Message}\n", LogColor.Red);
                AppendLog("→ 请确认 OpenSSH Client 已安装\n", LogColor.Cyan);
                return;
            }
            if (process is null) { AppendLog("无法启动 ssh 进程，请确认 OpenSSH Client 已安装\n", LogColor.Red); return; }
            var err = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            if (process.ExitCode == 0)
            {
                AppendLog("\n本机密钥登录成功！配置正确！\n", LogColor.Green);
            }
            else
            {
                AppendLog($"\n本机密钥登录失败（退出码 {process.ExitCode}）\n", LogColor.Red);
                if (!string.IsNullOrWhiteSpace(err))
                    AppendLog($"  错误详情: {err.Trim()}\n", LogColor.Red);
                AppendLog("→ 请检查 sshd 服务运行状态和 sshd_config 设置\n", LogColor.Cyan);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"\n测试失败：{ex.Message}\n", LogColor.Red);
        }
    }

    // 检查 ssh-keygen 是否真正可运行
    // 注意：Windows 原生 ssh-keygen 对 -? 返回退出码 1（非 0），Git/MSYS2 版本返回 0
    private static bool CanRunSshKeygen()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FindSshKeygen(),
                Arguments = "-?",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            // 退出码 0 或 1 均表示 ssh-keygen 可运行（打印了帮助信息）
            // 只有在进程完全无法启动时（如文件不存在）才认为不可用
            return p.ExitCode == 0 || p.ExitCode == 1;
        }
        catch { return false; }
    }

    private void EnsureOpenSshClient()
    {
        if (CanRunSshKeygen())
        {
            AppendLog("OpenSSH Client 已可用\n", LogColor.Green);
            return;
        }

        AppendLog("未检测到 OpenSSH Client，正在安装...\n", LogColor.Yellow);
        RunPowershellCommand(
            "Get-WindowsCapability -Online | Where-Object { $_.Name -like 'OpenSSH.Client*' -and $_.State -ne 'Installed' } | Add-WindowsCapability -Online",
            "OpenSSH Client 安装完成");
    }

    // ==================== 辅助方法 ====================
    private void TrySetAuthorizedKeysPermission()
    {
        if (!File.Exists(AuthKeysPath)) return;
        try
        {
            var fileInfo = new FileInfo(AuthKeysPath);
            var security = fileInfo.GetAccessControl();

            // 禁用继承并移除现有规则
            security.SetAccessRuleProtection(true, false);

            // 仅添加当前用户的读取权限
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name,
                                                FileSystemRights.Read,
                                                AccessControlType.Allow);
            security.SetAccessRule(rule);

            fileInfo.SetAccessControl(security);
            AppendLog("authorized_keys ACL 权限设置完成\n", LogColor.Green);
        }
        catch (Exception ex)
        {
            AppendLog($"authorized_keys ACL 设置失败：{ex.Message}\n", LogColor.Yellow);
        }
    }

    private void RunPowershellCommand(string script, string successMsg)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"{script}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi);
        if (process is null) { AppendLog("无法启动 PowerShell 进程\n", LogColor.Red); return; }
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            if (!string.IsNullOrEmpty(successMsg)) AppendLog(successMsg + " [完成]\n", LogColor.Green);
        }
        else
        {
            AppendLog("执行失败: " + process.StandardError.ReadToEnd() + "\n", LogColor.Red);
        }
    }

    private void LoadLocalIPs()
    {
        // 与 WebDAV 模块共用同一套枚举与排序（PocketNAS.Shared.LanAddresses）
        var addresses = LanAddresses.Get();
        IpSelector.Items.Clear();
        foreach (var address in addresses)
            IpSelector.Items.Add(new ComboBoxItem { Content = address.ToString(), Tag = address.Ip });
        if (IpSelector.Items.Count > 0)
            IpSelector.SelectedIndex = 0;
    }

    /// <summary>当前选中的 IP：下拉文本带网卡名，真正取值走 Tag</summary>
    private string GetSelectedIp() => IpSelector.SelectedItem switch
    {
        ComboBoxItem { Tag: string ip } => ip,
        string ip => ip,
        _ => "",
    };

    private void IpSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var ip = GetSelectedIp();
        if (ip.Length > 0)
            AppendLog($"已选择局域网地址: {ip}\n", LogColor.Cyan);
    }

    private void CopySelectedIp(object sender, MouseButtonEventArgs e)
    {
        var ip = GetSelectedIp();
        if (ip.Length > 0)
        {
            if (Win32ClipboardSetText(ip))
                AppendLog($"已复制 IP: {ip}\n", LogColor.Cyan);
            else
                AppendLog("复制失败：剪贴板被其他进程占用\n", LogColor.Red);
        }
    }

    // ==================== PocketNAS 扫码导入 ====================
    //
    // 二维码内容是 PocketNAS（安卓端文件管理器）DavQr 认识的 JSON：
    //   {"v":1,"type":"ssh","name":...,"url":"sftp://<ip>:22/","user":...,"pass":"","fp":"SHA256:...","key":"<PEM>"}
    // key 是完整 OpenSSH 私钥文本（本工具生成的 ed25519 无口令），手机扫到后落盘并按密钥认证直连。
    // 前提：sshd 已在运行（一键配置后生效）；地址取 IpSelector 当前选中项（物理网卡或虚拟组网地址都照原样用）。

    private async void BtnQr_Click(object sender, RoutedEventArgs e)
    {
        BtnQr.IsEnabled = false;
        try
        {
            // UI 控件的值必须在 UI 线程先取好再进后台线程（bb581d5 修过的同类坑，别再踩）
            var user = TxtUser.Text;
            var ip = GetSelectedIp();
            var payload = await Task.Run(() => BuildPocketNasPayload(user, ip));
            if (payload != null)
            {
                ShowQr(payload);

                // 出码的同时把地址 / 用户名写进剪贴板。SSH 走密钥认证，本来就没有密码；
                // 私钥也不往这里塞——它有单独的「复制私钥」入口，几百行糊到剪贴板只会碍事
                var copied = ClipboardText.Set($"sftp://{ip}:22/\n{user}");
                AppendLog(copied
                        ? $"地址和用户名已复制到剪贴板：sftp://{ip}:22/ · {user}\n"
                        : "复制到剪贴板失败：剪贴板被其他进程占用\n",
                    copied ? LogColor.Green : LogColor.Red);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"生成二维码失败：{ex.Message}\n", LogColor.Red);
        }
        finally
        {
            BtnQr.IsEnabled = true;
        }
    }

    /// <summary>拼 PocketNAS 连接 JSON；前提不满足时打日志并返回 null。
    /// user / ip 由调用方在 UI 线程取好传入，这里只做纯计算与文件、服务访问。</summary>
    private string? BuildPocketNasPayload(string user, string? ip)
    {
        // 1. sshd 必须在运行 —— 服务没起来，码里的地址扫了也连不上
        try
        {
            using var sc = new ServiceController("sshd");
            if (sc.Status != ServiceControllerStatus.Running)
            {
                Dispatcher.Invoke(() => AppendLog(
                    $"sshd 未运行（当前状态 {sc.Status}）。请先「一键配置并启动」，服务跑起来后二维码才生效。\n",
                    LogColor.Yellow));
                return null;
            }
        }
        catch (InvalidOperationException)
        {
            Dispatcher.Invoke(() => AppendLog(
                "本机没有 sshd 服务（OpenSSH Server 未安装）。请先「一键配置并启动」。\n", LogColor.Red));
            return null;
        }

        // 2. 私钥文件
        if (!File.Exists(PrivateKeyPath))
        {
            Dispatcher.Invoke(() => AppendLog(
                $"找不到私钥 {PrivateKeyPath}。请先「一键配置并启动」生成并部署密钥。\n", LogColor.Red));
            return null;
        }
        var keyText = File.ReadAllText(PrivateKeyPath).Trim();
        if (!keyText.Contains("PRIVATE KEY"))
        {
            Dispatcher.Invoke(() => AppendLog("私钥文件内容异常（不是 PEM 私钥文本）。\n", LogColor.Red));
            return null;
        }

        // 3. 地址：调用方在 UI 线程取好的 IpSelector 选中项，物理/虚拟组网地址都照原样用，不做网段过滤
        if (string.IsNullOrWhiteSpace(ip))
        {
            Dispatcher.Invoke(() => AppendLog("没有可用的局域网地址，无法生成二维码。\n", LogColor.Red));
            return null;
        }

        // 4. 主机指纹：读 sshd 的主机公钥算 SHA256（ed25519 → ecdsa → rsa 依次找）。
        //    取不到不阻塞，fp 留空，手机首连时手动核对一次即可
        var fp = GetHostFingerprint();
        if (fp == null)
            Dispatcher.Invoke(() => AppendLog(
                "没能读到主机指纹，二维码不带 fp；手机首次连接时会弹出指纹核对，确认即可。\n", LogColor.Yellow));

        var payload = JsonSerializer.Serialize(new
        {
            v = 1,
            type = "ssh",
            name = Environment.MachineName,
            url = $"sftp://{ip}:22/",
            // 与 FTPS / WebDAV 两处出码同构：url 给只认 url 的旧版 PocketNAS，
            // host/port 给按散字段拼地址的新版 PocketNAS
            host = ip,
            port = 22,
            user = user,      // 用 UI 线程取好的入参，别再读 TxtUser
            pass = "",
            fp = fp ?? "",
            key = keyText,
        });

        Dispatcher.Invoke(() => AppendLog(
            $"二维码已生成：{user}@{ip}:22（{(fp != null ? "含主机指纹" : "不含主机指纹")}，含私钥）\n",
            LogColor.Green));
        return payload;
    }

    /// <summary>ssh-keygen -lf 读 sshd 主机公钥指纹，形如 "SHA256:xxxx"（与手机端算法一致）</summary>
    private string? GetHostFingerprint()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var algo in new[] { "ed25519", "ecdsa", "rsa" })
        {
            var pub = Path.Combine(programData, "ssh", $"ssh_host_{algo}_key.pub");
            if (!File.Exists(pub)) continue;
            try
            {
                var keygen = FindSshKeygen();
                if (keygen == null) return null;
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = keygen,
                    Arguments = $"-lf \"{pub}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                var output = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(3000);
                // 输出形如 "256 SHA256:AbCdEf... comment (ED25519)"
                var token = output?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(t => t.StartsWith("SHA256:"));
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
            catch
            {
                // 换下一种算法再试
            }
        }
        return null;
    }

    /// <summary>生成二维码：私钥只在弹出的窗口里显示，主界面不落任何图像</summary>
    private void ShowQr(string payload)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        lastPayload = payload;

        TxtQrHint.Text = $"二维码 {data.ModuleMatrix.Count}×{data.ModuleMatrix.Count} 模块已弹出 —— " +
            "扫弹窗里那张；关掉后可按下面按钮重新弹出";
        BtnQrAgain.Visibility = Visibility.Visible;

        OpenQrWindow(data, payload);
    }

    /// <summary>重新弹出刚才那张二维码（窗口被关掉时用）</summary>
    private void BtnQrAgain_Click(object sender, RoutedEventArgs e)
    {
        if (lastPayload == null) return;
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(lastPayload, QRCodeGenerator.ECCLevel.M);
        OpenQrWindow(data, lastPayload);
    }

    /// <summary>当前二维码内容，重新弹窗时复用</summary>
    private string? lastPayload;

    /** 弹出的二维码窗口；重复生成时先关掉旧的 */
    private Window? qrWindow;

    /// <summary>弹出二维码窗口：右栏再宽也不如独立窗口好用。
    /// 尺寸按屏幕可用高度尽量给大——码的物理尺寸越大，手机在同样距离下
    /// 拍到的模块像素越多，摩尔纹的影响也相对越小，越好扫。</summary>
    private void OpenQrWindow(QRCodeData data, string payload)
    {
        qrWindow?.Close();
        lastPayload = payload;

        // 目标 760px，但别超过屏幕可用高度，小屏笔记本也不至于顶出屏幕
        var workH = SystemParameters.WorkArea.Height;
        var px = (int)Math.Min(760, workH - 180);
        if (px < 420) px = 420;

        var img = new System.Windows.Controls.Image
        {
            Source = RenderQr(data, px),
            Stretch = System.Windows.Media.Stretch.Uniform,
            Width = px,
            Height = px,
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            img, System.Windows.Media.BitmapScalingMode.NearestNeighbor);

        var panel = new StackPanel { Background = System.Windows.Media.Brushes.White };
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock
        {
            Text = "用 PocketNAS「SSH → 扫码添加」扫描 · 二维码含私钥，关闭窗口即销毁，请勿截屏分享",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Black,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 12),
        });

        var win = new Window
        {
            Title = "PocketNAS 扫码导入（关闭即销毁）",
            Content = panel,
            Width = px + 60,
            Height = px + 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = System.Windows.Media.Brushes.White,
            ResizeMode = ResizeMode.NoResize,
        };
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(qrWindow, win)) qrWindow = null;
        };
        qrWindow = win;
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

    // 日志统一交给外壳的全局日志面板；这里保留原签名，调用点不用动
    private void AppendLog(string text, LogColor color)
    {
        var level = color switch
        {
            LogColor.Green => LogLevel.Success,
            LogColor.Yellow => LogLevel.Warn,
            LogColor.Red => LogLevel.Error,
            _ => LogLevel.Info,
        };
        LogService.Write("SSH", text.TrimEnd(), level);
    }
}
