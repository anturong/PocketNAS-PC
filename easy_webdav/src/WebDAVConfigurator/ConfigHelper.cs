using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebDAVConfigurator;

public class UserInfo
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
    [JsonPropertyName("readonly")]
    public bool ReadOnly { get; set; } = false;
}

public class WebDAVConfigData
{
    private static readonly string BasePath = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

    /// <summary>
    /// 配置文件位置：服务以自身目录为基准读配置，所以并入 PocketNAS-PC 后
    /// 必须写到 service 子目录，否则界面保存的配置服务永远读不到。
    /// </summary>
    private static readonly string ConfigFilePath = ResolveConfigFilePath();

    private static string ResolveConfigFilePath()
    {
        var serviceDir = Path.Combine(BasePath, "service");
        var dir = File.Exists(Path.Combine(serviceDir, "WebDAVService.exe")) ? serviceDir : BasePath;
        return Path.Combine(dir, "webdav_config.json");
    }

    /// <summary>配置文件所在目录：所有相对路径都以它为基准，界面与服务两端保持一致</summary>
    private static string ConfigDir => Path.GetDirectoryName(ConfigFilePath)!;

    /// <summary>当前使用的配置文件完整路径（用于日志与排查）</summary>
    public static string ConfigPath => ConfigFilePath;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "+";
    [JsonPropertyName("enable_http")]
    public bool EnableHttp { get; set; } = true;
    [JsonPropertyName("port")]
    public int Port { get; set; } = 8080;
    [JsonPropertyName("enable_https")]
    public bool EnableHttps { get; set; } = false;
    [JsonPropertyName("https_port")]
    public int HttpsPort { get; set; } = 8443;
    [JsonPropertyName("cert_thumbprint")]
    public string? CertThumbprint { get; set; }
    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = "webdav_share";
    [JsonPropertyName("users")]
    public List<UserInfo> Users { get; set; } = new()
    {
        new UserInfo { Username = "admin", Password = "", ReadOnly = false }
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static WebDAVConfigData Load()
    {
        if (!File.Exists(ConfigFilePath))
        {
            var config = new WebDAVConfigData();
            config.Users[0].Password = GenerateRandomPassword();
            config.Save();
            System.Windows.MessageBox.Show(
                $"WebDAV 配置文件已生成：{ConfigFilePath}\n\n默认用户名：{config.Users[0].Username}\n默认密码：{config.Users[0].Password}\n\n请及时修改密码！",
                "初始配置已生成",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return config;
        }
        var json = File.ReadAllText(ConfigFilePath);
        var loaded = JsonSerializer.Deserialize<WebDAVConfigData>(json) ?? new WebDAVConfigData();
        if (!Path.IsPathRooted(loaded.RootDir))
        {
            // 相对路径一律相对配置文件所在目录解析，界面与服务两端结果一致
            loaded.RootDir = Path.GetFullPath(Path.Combine(ConfigDir, loaded.RootDir));
        }
        else
        {
            // 旧版本把默认共享夹存成了程序目录下的绝对路径，这里收回配置目录
            var legacy = Path.GetFullPath(Path.Combine(BasePath, "webdav_share")).TrimEnd('\\');
            if (string.Equals(Path.GetFullPath(loaded.RootDir).TrimEnd('\\'), legacy, StringComparison.OrdinalIgnoreCase))
                loaded.RootDir = Path.GetFullPath(Path.Combine(ConfigDir, "webdav_share"));
        }
        return loaded;
    }

    public void Save()
    {
        // 落在配置目录内的共享夹存相对路径，整个程序目录可以整体搬走
        var absoluteRoot = RootDir;
        try
        {
            if (TryGetRelative(RootDir, ConfigDir, out var relative))
                RootDir = relative;

            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigFilePath, json);
        }
        finally
        {
            RootDir = absoluteRoot;
        }
    }

    /// <summary>path 位于 baseDir 之内时给出相对路径；本身就是相对路径则原样返回</summary>
    private static bool TryGetRelative(string path, string baseDir, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (!Path.IsPathRooted(path))
        {
            relative = path;
            return true;
        }

        var full = Path.GetFullPath(path).TrimEnd('\\');
        var root = Path.GetFullPath(baseDir).TrimEnd('\\');
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        relative = Path.GetRelativePath(root, full);
        return true;
    }

    private static string GenerateRandomPassword()
    {
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string digits = "0123456789";
        const string special = "@#$%&";
        const string all = lower + upper + digits + special;
        var data = RandomNumberGenerator.GetBytes(16);
        var chars = new char[12];
        chars[0] = lower[data[0] % lower.Length];
        chars[1] = upper[data[1] % upper.Length];
        chars[2] = digits[data[2] % digits.Length];
        chars[3] = special[data[3] % special.Length];
        for (int i = 4; i < 12; i++)
            chars[i] = all[data[i] % all.Length];
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(256)).ToArray());
    }
}
