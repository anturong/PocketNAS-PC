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
    private static readonly string ConfigFilePath = Path.Combine(BasePath, "webdav_config.json");

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
    public string RootDir { get; set; } = Path.Combine(BasePath, "webdav_share");
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
            loaded.RootDir = Path.Combine(BasePath, loaded.RootDir);
        return loaded;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(ConfigFilePath, json);
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
