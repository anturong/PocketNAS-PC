using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FtpsServer;

public sealed class FtpsUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("readonly")]
    public bool ReadOnly { get; set; }
}

/// <summary>
/// FTPS 配置。与 WebDAV 模块保持一致的两条约定：
/// 1. 相对路径一律相对配置文件所在目录解析，整个目录可以整体搬走
/// 2. 配置文件缺失时自动生成一份带随机密码的默认配置
/// </summary>
public sealed class FtpsOptions
{
    [JsonPropertyName("listen_ip")]
    public string ListenIp { get; set; } = "0.0.0.0";

    [JsonPropertyName("explicit_port")]
    public int ExplicitPort { get; set; } = 21;

    [JsonPropertyName("implicit_port")]
    public int ImplicitPort { get; set; } = 990;

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = "ftps_share";

    [JsonPropertyName("pasv_min")]
    public int PasvMin { get; set; } = 50000;

    [JsonPropertyName("pasv_max")]
    public int PasvMax { get; set; } = 51000;

    [JsonPropertyName("require_tls")]
    public bool RequireTls { get; set; } = true;

    [JsonPropertyName("cert_file")]
    public string CertFile { get; set; } = "ftps_cert.pfx";

    [JsonPropertyName("users")]
    public List<FtpsUser> Users { get; set; } = new();

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static FtpsOptions Load(string configPath)
    {
        var path = Path.GetFullPath(configPath);
        if (!File.Exists(path))
        {
            var created = new FtpsOptions
            {
                Users = new List<FtpsUser>
                {
                    new() { Username = "ftpusr", Password = RandomPassword() },
                },
            };
            created.FilePath = path;
            created.Save();
            Console.WriteLine($"[*] 未找到配置，已生成默认配置: {path}");
            return created;
        }

        var options = JsonSerializer.Deserialize<FtpsOptions>(File.ReadAllText(path)) ?? new FtpsOptions();
        options.FilePath = path;
        if (options.Users.Count == 0)
        {
            var generated = new FtpsUser { Username = "ftpusr", Password = RandomPassword() };
            options.Users.Add(generated);
            options.Save();
            Console.WriteLine($"[*] 配置里没有用户，已生成默认账号 {generated.Username} / {generated.Password}（已写回配置）");
        }
        return options;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>共享目录：相对路径相对配置文件所在目录解析</summary>
    public string ResolvedRootDir()
    {
        var baseDir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
        return Path.IsPathRooted(RootDir) ? RootDir : Path.GetFullPath(Path.Combine(baseDir, RootDir));
    }

    /// <summary>落在配置目录内时存相对路径，否则保留绝对路径</summary>
    public void NormalizeRootDir()
    {
        var baseDir = Path.GetDirectoryName(FilePath);
        if (baseDir == null || !Path.IsPathRooted(RootDir)) return;

        var full = Path.GetFullPath(RootDir).TrimEnd('\\');
        var root = Path.GetFullPath(baseDir).TrimEnd('\\');
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            RootDir = Path.GetRelativePath(root, full);
    }

    public string ResolvedCertPath()
    {
        var baseDir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
        return Path.IsPathRooted(CertFile) ? CertFile : Path.GetFullPath(Path.Combine(baseDir, CertFile));
    }

    private static string RandomPassword()
    {
        const string pool = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        return new string(bytes.Select(b => pool[b % pool.Length]).ToArray());
    }
}
