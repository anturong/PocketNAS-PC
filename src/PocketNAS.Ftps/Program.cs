using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FtpsServer;

/// <summary>
/// 极简 FTPS 服务器 (RFC 4217 FTP over TLS)
/// 功能: USER/PASS 认证 / AUTH TLS 强制加密 / PBSZ+PROT P 数据通道加密
///       PASV+EPSV 被动模式 / LIST+NLST+MLSD / RETR 下载 / STOR 上传
///       TYPE A|I / PWD CWD CDUP / SIZE / SYST FEAT OPTS NOOP QUIT
/// 用法: FtpsServer.exe [端口] [根目录]
/// </summary>
public static class Program
{
    // ================= 可配置 =================
    static string RootDir = @"C:\FTPSroot";
    static string ListenIp = "192.168.3.200";
    static int ListenPort = 21;              // 显式 FTPS (AUTH TLS)
    static int ImplicitPort = 990;           // 隐式 FTPS (直接 TLS，VLC 默认走这里)
    static string User = "ftpusr";
    static string Pass = "Ftp123456";        // 无特殊字符，避免 URL 转义问题
    static int PasvMin = 50000, PasvMax = 51000;
    static string CertPath = Path.Combine(AppContext.BaseDirectory, "ftps_cert.pfx");
    // ==========================================

    public static async Task Main(string[] args)
    {
        if (args.Length >= 1 && int.TryParse(args[0], out int p)) ListenPort = p;
        if (args.Length >= 2) RootDir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(RootDir);

        var cert = LoadOrCreateCert();

        // 显式 FTPS 监听器 (21)
        var listener = new TcpListener(IPAddress.Parse(ListenIp), ListenPort);
        listener.Start();
        // 隐式 FTPS 监听器 (990，VLC 默认)
        var implicitListener = new TcpListener(IPAddress.Parse(ListenIp), ImplicitPort);
        implicitListener.Start();
        Console.WriteLine("==============================================");
        Console.WriteLine($" 显式FTPS: ftps://{ListenIp}:{ListenPort}/  (AUTH TLS)");
        Console.WriteLine($" 隐式FTPS: ftps://{ListenIp}:{ImplicitPort}/  (VLC 默认用这个)");
        Console.WriteLine($" 用户: {User}   密码: {Pass}");
        Console.WriteLine($" 根目录: {RootDir}");
        Console.WriteLine($" 被动端口: {PasvMin}-{PasvMax}");
        Console.WriteLine($" 证书: {CertPath}");
        Console.WriteLine(" Ctrl+C 停止");
        Console.WriteLine("==============================================");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // 并行接受两个端口的连接
        var acceptExplicit = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(() => HandleClient(client, cert, cts.Token, implicitTls: false), cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[!] 显式Accept: {ex.Message}"); }
            }
        }, cts.Token);

        var acceptImplicit = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var client = await implicitListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(() => HandleClient(client, cert, cts.Token, implicitTls: true), cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[!] 隐式Accept: {ex.Message}"); }
            }
        }, cts.Token);

        await Task.WhenAll(acceptExplicit, acceptImplicit);
    }

    // ================= 证书 =================
    static X509Certificate2 LoadOrCreateCert()
    {
        if (File.Exists(CertPath))
        {
            Console.WriteLine($"[*] 使用已有证书");
            return new X509Certificate2(CertPath, (string?)null, X509KeyStorageFlags.Exportable);
        }
        Console.WriteLine("[*] 生成自签名证书(10年)...");
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={ListenIp}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(ListenIp));
        req.CertificateExtensions.Add(san.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(10));
        var pfx = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(CertPath, pfx);
        Console.WriteLine($"[*] 证书已生成");
        return new X509Certificate2(pfx);
    }

    // ================= 会话 =================
    static async Task HandleClient(TcpClient client, X509Certificate2 cert, CancellationToken ct, bool implicitTls)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Console.WriteLine($"[+] {remote} ({(implicitTls ? "隐式990" : "显式21")})");
        Session? session = null;
        try
        {
            var raw = client.GetStream();
            session = new Session(client, raw, ct);

            bool tlsActive = false;
            bool authenticated = false;
            bool dataTls = false;
            string? pendingUser = null;
            string cwd = "/";
            char type = 'A';
            long restOffset = 0;   // REST 断点续传偏移（VLC 快进用）

            if (implicitTls)
            {
                // 隐式 FTPS: 连接后立即建立 TLS，再发 banner
                await session.UpgradeToTls(cert);
                tlsActive = true;
                dataTls = true;
                Console.WriteLine($"[{remote}] 隐式TLS已建立");
            }
            await session.SendAsync("220 FTPS Server ready.");

            while (!ct.IsCancellationRequested && client.Connected)
            {
                string? line = await session.ReadLineAsync();
                if (line == null) break;
                Console.WriteLine($"[{remote}] {line}");

                var sp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var cmd = sp[0].ToUpperInvariant();
                var arg = line.Length > sp[0].Length ? line[(sp[0].Length + 1)..].Trim() : "";

                switch (cmd)
                {
                    // ---------- TLS ----------
                    case "AUTH" when arg.ToUpperInvariant() == "TLS":
                        if (tlsActive) { await session.SendAsync("503 Already using TLS."); break; }
                        await session.SendAsync("234 Proceed with TLS negotiation.");
                        await session.UpgradeToTls(cert);
                        tlsActive = true;
                        dataTls = true;
                        Console.WriteLine($"[{remote}] TLS 已建立");
                        break;

                    case "PBSZ":
                        await session.SendAsync(tlsActive ? "200 PBSZ=0" : "503 TLS required.");
                        break;

                    case "PROT":
                        if (!tlsActive) { await session.SendAsync("503 TLS required."); break; }
                        if (arg.ToUpperInvariant() == "P") { dataTls = true; await session.SendAsync("200 Protection level set to P"); }
                        else if (arg.ToUpperInvariant() == "C") { dataTls = false; await session.SendAsync("200 Protection level set to C"); }
                        else await session.SendAsync("536 Unsupported PROT level.");
                        break;

                    // ---------- 认证 ----------
                    case "USER":
                        if (authenticated) { await session.SendAsync("503 Already logged in."); break; }
                        pendingUser = arg;
                        await session.SendAsync("331 Password required for " + arg + ".");
                        break;

                    case "PASS":
                        if (authenticated) { await session.SendAsync("503 Already logged in."); break; }
                        if (pendingUser == User && arg == Pass)
                        {
                            authenticated = true;
                            await session.SendAsync("230 Logged in.");
                        }
                        else
                        {
                            pendingUser = null;
                            await session.SendAsync("530 Login incorrect.");
                        }
                        break;

                    // ---------- 被动模式 ----------
                    case "PASV":
                        if (!authenticated) { await session.SendAsync("530 Not logged in."); break; }
                        await session.OpenPassiveAsync(PasvMin, PasvMax, ListenIp, cmd);
                        break;

                    case "EPSV":
                        if (!authenticated) { await session.SendAsync("530 Not logged in."); break; }
                        // EPSV ALL: 声明支持EPSV，回复200即可，不打开监听
                        if (arg.ToUpperInvariant() == "ALL") { await session.SendAsync("200 EPSV ALL ok."); break; }
                        await session.OpenPassiveAsync(PasvMin, PasvMax, ListenIp, cmd);
                        break;

                    case "ABOR":
                        await session.AbortDataAsync();
                        // VLC 3.0.24 ftp_StopStream(seek/关闭时)顺序:
                        //   1.发ABOR 2.关数据通道 3.读RETR最终响应(226) 4.读ABOR响应(226)
                        // 因此 ABOR 必须总是回 226，否则第4步超时 -> "response failure" -> seek失败
                        await session.SendAsync("226 ABOR successful.");
                        break;

                    // ---------- 类型 ----------
                    case "TYPE":
                        if (arg.ToUpperInvariant().StartsWith('A')) { type = 'A'; await session.SendAsync("200 Type set to A"); }
                        else if (arg.ToUpperInvariant().StartsWith('I')) { type = 'I'; await session.SendAsync("200 Type set to I"); }
                        else await session.SendAsync("504 Unsupported type.");
                        break;

                    // ---------- 目录 ----------
                    case "PWD":
                        await session.SendAsync($"257 \"{cwd}\" is current directory.");
                        break;

                    case "CWD":
                        {
                            var target = ResolvePath(cwd, arg, RootDir);
                            if (Directory.Exists(target))
                            {
                                cwd = Normalize(cwd, arg);
                                await session.SendAsync("250 Directory changed to " + cwd);
                            }
                            else if (File.Exists(target))
                            {
                                // 兼容 VLC: 对文件执行CWD返回成功，但不改工作目录
                                await session.SendAsync("250 Directory changed.");
                            }
                            else await session.SendAsync("550 Directory not found.");
                        }
                        break;

                    case "CDUP":
                        cwd = "/"; await session.SendAsync("250 Directory changed to /");
                        break;

                    // ---------- 列表 ----------
                    case "LIST":
                    case "NLST":
                    case "MLSD":
                        {
                            if (!authenticated) { await session.SendAsync("530 Not logged in."); break; }
                            var target = ResolvePath(cwd, arg == "/" ? "" : arg, RootDir);
                            if (!Directory.Exists(target)) { await session.SendAsync("550 Directory not found."); break; }
                            // 先发150再Accept，避免被动模式死锁
                            await session.SendAsync("150 Opening data connection.");
                            var dp = await session.AcceptDataAsync(dataTls, cert);
                            if (dp == null) { await session.SendAsync("425 Can't open data connection."); break; }
                            await SendListAsync(dp, target, cmd, type, cwd);
                            if (dp is SslStream sslDp) { try { await sslDp.ShutdownAsync(); } catch { } }
                            dp.Close();
                            await session.SendAsync("226 Transfer complete.");
                        }
                        break;

                    // ---------- 断点续传 (VLC 快进 seek 依赖) ----------
                    case "REST":
                        {
                            if (!authenticated) { await session.SendAsync("530 Not logged in."); break; }
                            if (long.TryParse(arg, out var off) && off >= 0)
                            {
                                restOffset = off;
                                await session.SendAsync($"350 Restarting at {off}.");
                            }
                            else await session.SendAsync("501 Bad REST parameter.");
                        }
                        break;

                    // ---------- 下载 ----------
                    case "RETR":
                        {
                            if (!authenticated) { await session.SendAsync("530 Not logged in."); break; }
                            var f = ResolvePath(cwd, arg, RootDir);
                            if (!File.Exists(f)) { await session.SendAsync("550 File not found."); break; }
                            await session.SendAsync("150 Opening data connection.");
                            var dp = await session.AcceptDataAsync(dataTls, cert);
                            if (dp == null) { restOffset = 0; await session.SendAsync("425 Can't open data connection."); break; }
                            try
                            {
                                await using (var fs = File.OpenRead(f))
                                {
                                    if (restOffset > 0)
                                    {
                                        fs.Seek(restOffset, SeekOrigin.Begin);
                                        Console.WriteLine($"[{remote}] REST 从偏移 {restOffset} 续传");
                                    }
                                    if (type == 'A') { var txt = await new StreamReader(fs, Encoding.UTF8).ReadToEndAsync(ct); txt = txt.Replace("\r\n", "\n").Replace("\n", "\r\n"); var b = Encoding.UTF8.GetBytes(txt); await dp.WriteAsync(b, ct); }
                                    else await fs.CopyToAsync(dp, ct);
                                }
                            }
                            catch (IOException)
                            {
                                // 客户端(如VLC探测/快进)提前关闭数据通道：
                                // VLC 打开URL时会做探测传输(读一点就关数据通道)，
                                // 必须立即回 226 表示传输完成(RETR最终响应)，否则VLC判定文件不可读。
                                // 随后的 ABOR 会另回 226 (VLC ftp_StopStream 第4步需要)。
                                Console.WriteLine($"[{remote}] 数据通道被客户端提前关闭 (VLC seek 正常行为)");
                            }
                            restOffset = 0;
                            if (dp is SslStream sslDp) { try { await sslDp.ShutdownAsync(); } catch { } }
                            try { dp.Close(); } catch { }
                            await session.SendAsync("226 Transfer complete.");
                        }
                        break;

                    // ---------- 上传 ----------
                    case "STOR":
                        {
                            if (!authenticated) { await session.SendAsync("530 Not logged in."); break; }
                            var f = ResolvePath(cwd, arg, RootDir);
                            var dir = Path.GetDirectoryName(f);
                            if (dir == null || !Directory.Exists(dir)) { await session.SendAsync("550 Directory not found."); break; }
                            await session.SendAsync("150 Opening data connection.");
                            var dp = await session.AcceptDataAsync(dataTls, cert);
                            if (dp == null) { await session.SendAsync("425 Can't open data connection."); break; }
                            await using (var fs = File.Create(f))
                            {
                                if (type == 'A')
                                {
                                    using var sr = new StreamReader(dp, Encoding.UTF8);
                                    var txt = await sr.ReadToEndAsync(ct);
                                    txt = txt.Replace("\r\n", "\n");
                                    await fs.WriteAsync(Encoding.UTF8.GetBytes(txt), ct);
                                }
                                else await dp.CopyToAsync(fs, ct);
                            }
                            if (dp is SslStream sslDp) { try { await sslDp.ShutdownAsync(); } catch { } }
                            dp.Close();
                            await session.SendAsync("226 Transfer complete.");
                        }
                        break;

                    // ---------- 文件信息 ----------
                    case "SIZE":
                        {
                            var f = ResolvePath(cwd, arg, RootDir);
                            if (File.Exists(f)) await session.SendAsync($"213 {new FileInfo(f).Length}");
                            else await session.SendAsync("550 File not found.");
                        }
                        break;

                    case "MDTM":
                        {
                            var f = ResolvePath(cwd, arg, RootDir);
                            if (File.Exists(f)) await session.SendAsync($"213 {File.GetLastWriteTimeUtc(f):yyyyMMddHHmmss}");
                            else await session.SendAsync("550 File not found.");
                        }
                        break;

                    case "SYST":
                        await session.SendAsync("215 UNIX Type: L8");
                        break;

                    case "FEAT":
                        await session.SendAsync("211-Features:");
                        await session.SendAsync(" AUTH TLS");
                        await session.SendAsync(" PBSZ");
                        await session.SendAsync(" PROT");
                        await session.SendAsync(" EPSV");
                        await session.SendAsync(" PASV");
                        await session.SendAsync(" SIZE");
                        await session.SendAsync(" MLSD");
                        await session.SendAsync(" UTF8");
                        await session.SendAsync("211 End");
                        break;

                    case "OPTS":
                        await session.SendAsync("200 OK");
                        break;

                    case "NOOP":
                        await session.SendAsync("200 NOOP ok.");
                        break;

                    case "QUIT":
                        await session.SendAsync("221 Goodbye.");
                        return;

                    case "DELE":
                        {
                            var f = ResolvePath(cwd, arg, RootDir);
                            if (File.Exists(f)) { File.Delete(f); await session.SendAsync("250 File deleted."); }
                            else await session.SendAsync("550 File not found.");
                        }
                        break;

                    case "MKD":
                        {
                            var d = ResolvePath(cwd, arg, RootDir);
                            try { Directory.CreateDirectory(d); await session.SendAsync("257 Directory created."); }
                            catch { await session.SendAsync("550 Can't create directory."); }
                        }
                        break;

                    default:
                        await session.SendAsync("502 Command not implemented: " + cmd);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{remote}] 错误: {ex.Message}");
        }
        finally
        {
            Console.WriteLine($"[-] {remote}");
            if (session != null) { try { await session.DisposeAsync(); } catch { } }
            try { client.Close(); } catch { }
        }
    }

    // ================= 路径辅助 =================
    static string ResolvePath(string cwd, string arg, string root)
    {
        var full = arg.StartsWith('/') ? arg : (cwd == "/" ? "/" + arg : cwd.TrimEnd('/') + "/" + arg);
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var s in parts)
        {
            if (s == ".") continue;
            if (s == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(s);
        }
        var rel = string.Join(Path.DirectorySeparatorChar, stack);
        return string.IsNullOrEmpty(rel) ? root : Path.GetFullPath(Path.Combine(root, rel));
    }

    static string Normalize(string cwd, string arg)
    {
        var full = arg.StartsWith('/') ? arg : (cwd == "/" ? "/" + arg : cwd.TrimEnd('/') + "/" + arg);
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var s in parts)
        {
            if (s == ".") continue;
            if (s == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(s);
        }
        return "/" + string.Join("/", stack);
    }

    static async Task SendListAsync(Stream data, string dir, string cmd, char type, string cwd)
    {
        var entries = new List<(string name, string path, bool isDir, long size, DateTime mtime)>();
        foreach (var d in Directory.GetDirectories(dir))
            entries.Add((Path.GetFileName(d), d, true, 0, Directory.GetLastWriteTime(d)));
        foreach (var f in Directory.GetFiles(dir))
        {
            var fi = new FileInfo(f);
            entries.Add((fi.Name, f, false, fi.Length, fi.LastWriteTime));
        }

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (cmd == "NLST") { sb.AppendLine(e.name); continue; }
            if (cmd == "MLSD")
            {
                sb.Append($"type={(e.isDir ? "dir" : "file")};size={e.size};modify={e.mtime:yyyyMMddHHmmss}; ");
                sb.AppendLine(e.name);
                continue;
            }
            // LIST: unix 风格
            string perms = e.isDir ? "drwxr-xr-x" : "-rw-r--r--";
            string date = e.mtime.ToString("MMM dd HH:mm");
            sb.AppendLine($"{perms} 1 owner group {e.size,12} {date} {e.name}");
        }
        var bytes = type == 'A' ? Encoding.UTF8.GetBytes(sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n")) : Encoding.UTF8.GetBytes(sb.ToString());
        await data.WriteAsync(bytes);
    }
}

/// <summary>
/// 会话封装：统一发送/读取，支持 TLS 升级与被动数据连接
/// </summary>
public class Session : IAsyncDisposable
{
    readonly TcpClient _client;
    readonly Stream _raw;
    readonly CancellationToken _ct;
    Stream _stream;
    SslStream? _ssl;
    TcpListener? _pasv;
    readonly byte[] _buf = new byte[16384];
    readonly StringBuilder _lineBuf = new();

    public Session(TcpClient client, Stream raw, CancellationToken ct)
    {
        _client = client;
        _raw = raw;
        _stream = raw;
        _ct = ct;
    }

    public async Task SendAsync(string msg)
    {
        var b = Encoding.UTF8.GetBytes(msg + "\r\n");
        await _stream.WriteAsync(b, _ct);
        await _stream.FlushAsync(_ct);
        Console.WriteLine($"[{(_ssl != null ? "TLS" : "RAW")}] => {msg.Split('\n')[0]}");
    }

    public async Task<string?> ReadLineAsync()
    {
        while (true)
        {
            int n = await _stream.ReadAsync(_buf, _ct);
            if (n == 0) return null;
            _lineBuf.Append(Encoding.UTF8.GetString(_buf, 0, n));
            int idx = _lineBuf.ToString().IndexOf('\n');
            if (idx >= 0)
            {
                var line = _lineBuf.ToString(0, idx).TrimEnd('\r');
                _lineBuf.Remove(0, idx + 1);
                return line;
            }
        }
    }

    public async Task UpgradeToTls(X509Certificate2 cert)
    {
        _ssl = new SslStream(_raw, leaveInnerStreamOpen: false);
        await _ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false
        }, _ct);
        _stream = _ssl;
    }

    public async Task OpenPassiveAsync(int min, int max, string listenIp, string cmd)
    {
        try
        {
            // 从端口范围中找一个可用端口
            int port = 0;
            for (int i = min; i <= max; i++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, i);
                    l.Start();
                    port = i;
                    _pasv = l;
                    break;
                }
                catch { continue; }
            }
            if (port == 0) { await SendAsync("425 No free port."); return; }

            if (cmd == "EPSV")
                await SendAsync($"229 Entering Extended Passive Mode (|||{port}|).");
            else
            {
                var ip = listenIp.Replace('.', ',');
                await SendAsync($"227 Entering Passive Mode ({ip},{port / 256},{port % 256}).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] PASV: {ex.Message}");
            await SendAsync("425 Can't open passive connection.");
        }
    }

    public async Task<Stream?> AcceptDataAsync(bool dataTls, X509Certificate2 cert)
    {
        if (_pasv == null) return null;
        try
        {
            var tcp = await _pasv.AcceptTcpClientAsync(_ct);
            _pasv.Stop();
            _pasv = null;
            Stream s = tcp.GetStream();
            if (dataTls)
            {
                var ssl = new SslStream(s, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false
                }, _ct);
                s = ssl;
            }
            return s;
        }
        catch { return null; }
    }

    public Task AbortDataAsync()
    {
        try { _pasv?.Stop(); } catch { }
        _pasv = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try { _pasv?.Stop(); } catch { }
        if (_ssl != null)
        {
            try { await _ssl.ShutdownAsync(); } catch { }
            try { _ssl.Dispose(); } catch { }
        }
        await Task.CompletedTask;
    }
}
