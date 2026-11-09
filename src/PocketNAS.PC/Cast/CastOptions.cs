using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketNAS.PC.Cast;

/// <summary>
/// 「投屏接收」的持久化项：上次真正跑起来的端口、用户指定的默认播放器、手动添加的播放器。
/// 网卡按定案不落盘 —— 每次启动回到优先推荐项，别把两个设置的口径混在一起。
/// 程序目录可写就放程序目录；装到只读目录时降级到 %LocalAppData%\PocketNAS-PC\。
///
/// 注意：写端口必须走「先 Load 再改字段再整体 Save」，否则会把同一文件里的播放器设置冲掉。
/// </summary>
public sealed class CastOptions
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("defaultPlayer")]
    public string DefaultPlayer { get; set; } = "";

    [JsonPropertyName("manualPlayers")]
    public List<string> ManualPlayers { get; set; } = new();

    private static readonly string ProgramPath =
        Path.Combine(AppContext.BaseDirectory, "cast_config.json");

    private static readonly string FallbackPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PocketNAS-PC", "cast_config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>配置坏了就当没记录过，不能因为一个坏 JSON 让服务起不来。</summary>
    public static CastOptions Load()
    {
        foreach (var path in new[] { ProgramPath, FallbackPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var o = JsonSerializer.Deserialize<CastOptions>(File.ReadAllText(path));
                if (o is null) continue;
                o.DefaultPlayer ??= "";
                o.ManualPlayers ??= new List<string>();
                return o;
            }
            catch
            {
                // 换下一个落点
            }
        }
        return new CastOptions();
    }

    public static void Save(CastOptions options)
    {
        var json = JsonSerializer.Serialize(options, JsonOpts);
        foreach (var path in new[] { ProgramPath, FallbackPath })
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json);
                return;
            }
            catch
            {
                // 只写配置，失败不影响服务本身；换下一个落点
            }
        }
    }

    /// <summary>返回 0 表示还没有任何记录（首次启动），服务从 9528 起找空闲端口。</summary>
    public static int LoadPort() => Load().Port;

    public static void SavePort(int port)
    {
        var o = Load();
        o.Port = port;
        Save(o);
    }

    public static string LoadDefaultPlayer() => Load().DefaultPlayer;

    public static void SaveDefaultPlayer(string path)
    {
        var o = Load();
        o.DefaultPlayer = path ?? "";
        Save(o);
    }

    public static List<string> LoadManualPlayers() => Load().ManualPlayers;

    /// <summary>手动指定只增不删，路径不区分大小写去重。</summary>
    public static void AddManualPlayer(string path)
    {
        var o = Load();
        if (o.ManualPlayers.Any(p => string.Equals(p, path, System.StringComparison.OrdinalIgnoreCase)))
            return;
        o.ManualPlayers.Add(path);
        Save(o);
    }
}
