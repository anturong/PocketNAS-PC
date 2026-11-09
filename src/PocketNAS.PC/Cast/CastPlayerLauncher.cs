using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PocketNAS.Shared;

namespace PocketNAS.PC.Cast;

public sealed record PlayerInfo(string Name, string Path, string Source)
{
    public string Label => $"{Name} · {Source}";
}

/**
 * 外部播放器：自发现生成列表 + 用户手动指定，用户点「设为默认」后落盘。
 *
 * 口径（2026-11-09 第 18/20 条）：
 *  - 零新增依赖、不增体积；不做全盘扫描，只看注册表卸载表 + 常见路径 + PATH；
 *  - 投屏时把手机 NanoHTTPD 的媒体 URL 用命令行交给默认播放器，
 *    快进 / 进度条 / 音量 / 全屏全由播放器原生提供（媒体源已支持 Range 206）；
 *  - 只在「列表为空 / 默认失效 / 启动失败」时回退本程序内置播放窗口；
 *  - 只关自己启动的那个进程（记 PID），绝不误杀用户原本开着的播放器窗口；
 *  - VLC 传 --no-one-instance，保证启动时是独立进程，不会把 URL 转交给用户已开的实例。
 */
public static class CastPlayerLauncher
{
    private static readonly (string Match, string Exe)[] Known =
    {
        ("VLC", "vlc.exe"),
        ("PotPlayer", "PotPlayerMini64.exe"),
        ("MPC-HC", "mpc-hc64.exe"),
        ("mpv", "mpv.exe"),
        ("KMPlayer", "KMPlayer.exe"),
        ("Windows Media Player", "wmplayer.exe"),
    };

    private static readonly string[] CommonPaths =
    {
        @"C:\Program Files\VideoLAN\VLC\vlc.exe",
        @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
        @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe",
        @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini.exe",
        @"C:\Program Files\MPC-HC\mpc-hc64.exe",
        @"C:\Program Files (x86)\MPC-HC\mpc-hc.exe",
        @"C:\Program Files\mpv\mpv.exe",
        @"C:\Program Files\Windows Media Player\wmplayer.exe",
        @"C:\Program Files (x86)\Windows Media Player\wmplayer.exe",
    };

    private static Process? _current;

    public static bool IsPlaying => _current is not null && !SafeHasExited(_current);

    /** 自己启动的播放器退出时通知 UI 清状态；换片时靠 PID 比对忽略旧进程的退出。 */
    public static event Action? ActiveExited;

    /// <summary>自发现 + 手动添加，按 VLC → PotPlayer → … 顺序，路径去重。</summary>
    public static List<PlayerInfo> Discover()
    {
        var found = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string source)
        {
            if (!IsExe(path) || !File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (found.ContainsKey(full)) return;
            found[full] = new PlayerInfo(DisplayName(full), full, source);
        }

        foreach (var p in CommonPaths) Add(p, "自发现");
        ScanRegistry(Add);
        ScanPath(Add);
        foreach (var p in CastOptions.LoadManualPlayers()) Add(p, "手动添加");
        return found.Values.ToList();
    }

    private static void ScanRegistry(Action<string, string> add)
    {
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sk = key.OpenSubKey(sub);
                        if (sk is null) continue;
                        var name = sk.GetValue("DisplayName") as string ?? "";
                        if (!Known.Any(k => name.Contains(k.Match, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var icon = IconPath(sk.GetValue("DisplayIcon") as string);
                        if (icon is not null) add(icon, "自发现");
                        var dir = sk.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                        foreach (var k in Known)
                        {
                            var candidate = Path.Combine(dir.Trim().Trim('"'), k.Exe);
                            if (File.Exists(candidate)) add(candidate, "自发现");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void ScanPath(Action<string, string> add)
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var dir in raw.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var k in Known)
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), k.Exe);
                    if (File.Exists(p)) add(p, "自发现");
                }
                catch { }
            }
        }
    }

    /** DisplayIcon 常见形态："C:\...\vlc.exe",0 或 "C:\...\vlc.exe" -FileAssociation。 */
    private static string? IconPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"');
        var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) s = s[..(i + 4)].Trim().Trim('"');
        return IsExe(s) && File.Exists(s) ? s : null;
    }

    private static string DisplayName(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var name = v.FileDescription ?? v.ProductName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public static bool IsExe(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path.Trim().Trim('"')), ".exe", StringComparison.OrdinalIgnoreCase);

    /**
     * 决定这次用哪个播放器。没指定默认时暂用自发现列表第一项（并在界面 / 日志讲清楚），
     * 这样首次投屏就能用本机已装的播放器，而不是先退回内置窗口。
     */
    public static string? ResolveDefault(out string notice)
    {
        var configured = CastOptions.LoadDefaultPlayer();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured) && IsExe(configured))
        {
            notice = "使用已指定的默认播放器";
            return configured;
        }
        var first = Discover().FirstOrDefault();
        if (first is not null)
        {
            notice = string.IsNullOrEmpty(configured)
                ? "未指定默认，暂用自发现列表第一项；点「设为默认」可固定"
                : "原默认播放器已失效，暂用自发现列表第一项；请重新指定默认";
            return first.Path;
        }
        notice = "没有可用播放器，将使用本程序内置窗口";
        return null;
    }

    /// <summary>启动外部播放器。返回 false 表示调用方应回退内置窗口。</summary>
    public static bool Launch(string exe, string url)
    {
        CloseCurrent();
        if (!IsExe(exe) || !File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = true };
            // VLC 默认单实例：不隔离的话会把 URL 转交给用户已开的窗口，我们记的 PID 立刻失效。
            if (Path.GetFileName(exe).Equals("vlc.exe", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add("--no-one-instance");
            psi.ArgumentList.Add(url);

            var p = Process.Start(psi);
            if (p is null) return false;
            _current = p;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                // 换片 / 手动关闭会先把 _current 置空，旧进程的退出回调必须忽略。
                if (!ReferenceEquals(_current, p)) return;
                _current = null;
                ActiveExited?.Invoke();
            };
            LogService.Success("投屏接收", $"已交给外部播放器：{Path.GetFileName(exe)}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("投屏接收", $"外部播放器启动失败（{ex.GetType().Name}），回退内置窗口");
            return false;
        }
    }

    /** 只杀自己启动的进程，不 Kill 进程树，避免误杀用户原本开着的播放器。 */
    public static void CloseCurrent()
    {
        var p = _current;
        _current = null;
        if (p is null) return;
        try { if (!SafeHasExited(p)) p.Kill(); } catch { }
        try { p.Dispose(); } catch { }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }
}
