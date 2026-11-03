using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

public class UserInfo
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
    [JsonPropertyName("readonly")]
    public bool ReadOnly { get; set; } = false;
}

public class WebDAVConfig
{
    private static readonly string BasePath = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    private static readonly string ConfigFilePath = Path.Combine(BasePath, "webdav_config.json");

    public string Host { get; set; } = "+";
    public bool EnableHttp { get; set; } = true;
    public int Port { get; set; } = 8080;
    public string RootDir { get; set; } = Path.Combine(BasePath, "webdav_share");
    public bool EnableHttps { get; set; } = false;
    public int HttpsPort { get; set; } = 8443;
    public string? CertThumbprint { get; set; }
    public List<UserInfo> Users { get; set; } = new()
    {
        new UserInfo { Username = "admin", Password = "", ReadOnly = false }
    };

    public static WebDAVConfig Load()
    {
        var config = new WebDAVConfig();
        if (!File.Exists(ConfigFilePath))
        {
            config.Users[0].Password = GenerateRandomPassword();
            config.Save();
            Console.WriteLine($"WebDAV 配置文件已生成: {ConfigFilePath}");
            Console.WriteLine($"默认用户名: {config.Users[0].Username}");
            Console.WriteLine($"默认密码: {config.Users[0].Password}");
            Console.WriteLine("请及时修改密码！");
            config.EnsureRootDir();
            return config;
        }

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("host", out var h)) config.Host = h.GetString() ?? config.Host;
            if (root.TryGetProperty("enable_http", out var en)) config.EnableHttp = en.GetBoolean();
            if (root.TryGetProperty("port", out var p)) config.Port = p.GetInt32();
            if (root.TryGetProperty("root_dir", out var r))
            {
                var dir = r.GetString() ?? "";
                if (dir != "")
                {
                    config.RootDir = Path.IsPathRooted(dir) ? dir : Path.Combine(BasePath, dir);
                }
            }
            if (root.TryGetProperty("enable_https", out var eh)) config.EnableHttps = eh.GetBoolean();
            if (root.TryGetProperty("https_port", out var hp)) config.HttpsPort = hp.GetInt32();
            if (root.TryGetProperty("cert_thumbprint", out var ct)) config.CertThumbprint = ct.GetString();
            if (root.TryGetProperty("users", out var u) && u.ValueKind == JsonValueKind.Array)
            {
                var users = JsonSerializer.Deserialize<List<UserInfo>>(u.GetRawText());
                if (users != null && users.Count > 0)
                    config.Users = users;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取配置失败，使用默认配置: {ex.Message}");
        }

        if (!config.EnableHttp && !config.EnableHttps)
        {
            config.EnableHttp = true;
            Console.WriteLine("HTTP 和 HTTPS 均已禁用，已自动启用 HTTP。");
        }

        config.EnsureRootDir();
        return config;
    }

    private void EnsureRootDir()
    {
        if (!Directory.Exists(RootDir))
        {
            try { Directory.CreateDirectory(RootDir); }
            catch (Exception ex) { Console.WriteLine($"无法创建共享目录: {ex.Message}"); }
        }
    }

    public Dictionary<string, string> GetUserPasswordMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in Users) map[u.Username] = u.Password;
        return map;
    }

    public Dictionary<string, bool> GetReadOnlyMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in Users) map[u.Username] = u.ReadOnly;
        return map;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(new
        {
            host = Host,
            enable_http = EnableHttp,
            port = Port,
            root_dir = RootDir,
            enable_https = EnableHttps,
            https_port = HttpsPort,
            cert_thumbprint = CertThumbprint,
            users = Users.Select(u => new { username = u.Username, password = u.Password, @readonly = u.ReadOnly })
        }, new JsonSerializerOptions { WriteIndented = true });
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
