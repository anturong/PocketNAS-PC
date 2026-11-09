using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using PocketNAS.Shared;

namespace PocketNAS.PC.Cast;

/** 手机端 /cast 推过来的一条投送。 */
public sealed class CastRequest
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
}

/**
 * PC 侧「投屏接收」后台服务。
 *
 * 设计口径（2026-11-09）：
 *  - 不实现标准 DMR / AVTransport：手机端是自有 App，只需被发现 + 收到一个媒体 URL；
 *  - 设备地址全部运行时取得：网卡用 LanAddresses 的优先推荐项且用户可改（不落盘），
 *    端口优先用上次成功启动的值、被占自动顺延并提示，LOCATION 由这两者拼出；
 *    产品代码零地址常量（SSDP 组播地址与端口是协议常量）；
 *  - 端口事件（沿用上次 / 被占顺延 / 检测 / 全占 / 切换网卡）同时写 LogService 和设置页；
 *  - 双保险发现：能绑 UDP 1900 就回 M-SEARCH 单播响应，同时周期性发 NOTIFY alive；
 *    Windows 的 SSDPSRV 常占 1900，靠 NOTIFY 也能被手机 4.2 秒窗口收到；
 *  - HTTP 用 TcpListener 手写极简路由，不用 HttpListener（避免 netsh urlacl 提权）；
 *  - 只监听局域网请求，POST /cast 校验 URL 非 loopback，首次确认由设置页开关控制；
 *  - 本类不碰 UI，事件由 CastView 在 Dispatcher 里消费。
 */
public sealed class CastReceiver : IDisposable
{
    public const string StPc = "urn:pocketnas:device:CastTarget:1";

    public const int FirstPort = 9528;
    public const int PortSlots = 32;
    public static int LastPort => FirstPort + PortSlots - 1;

    /** 启动来源：决定端口事件怎么写日志，避免「使用上次端口」盖掉按钮操作的原因。 */
    public enum StartReason { Startup, ApplyNic, DetectFreePort, Toggle }

    private const string Group = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const int NotifyIntervalMs = 1200;

    public static CastReceiver Instance { get; } = new();

    private CancellationTokenSource? _cts;
    private TcpListener? _http;
    private UdpClient? _ssdp;
    private string _ip = "";
    private string _udn = "";
    private int _port;
    private bool _running;
    private bool _casting;

    public bool IsRunning => _running;
    public string Ip => _ip;
    public string NicName { get; private set; } = "";
    public int Port => _port;
    public bool HasActiveCast => _casting;

    /** 最近一条端口事件：设置页状态区直接读它，同时已写进右侧操作日志。 */
    public string PortNotice { get; private set; } = "";
    public LogLevel PortNoticeLevel { get; private set; } = LogLevel.Info;

    public string DescriptionUrl => $"http://{_ip}:{_port}/cast/desc.xml";
    public string DeviceName => $"PocketNAS-PC（{Environment.MachineName}）";

    /** 收到投送（后台线程触发，订阅方必须自己切 UI 线程）。 */
    public event Action<CastRequest, string>? CastRequested;

    /** 手机端请求停止播放。 */
    public event Action? StopRequested;

    /** 首次投送是否弹确认；封闭测试默认不弹，设置页可开。 */
    public bool RequireConfirm { get; set; }

    /** 一条端口事件同时进日志与设置页状态区，两处文案完全一致。 */
    public void SetNotice(string message, LogLevel level = LogLevel.Info)
    {
        PortNotice = message;
        PortNoticeLevel = level;
        LogService.Write("投屏接收", message, level);
    }

    public void MarkCastEnded() => _casting = false;
    public void MarkCastStarted() => _casting = true;

    /**
     * 程序启动默认入口：网卡每次回到 LanAddresses 的优先推荐项（不落盘、不记住上次），
     * 端口优先用上次成功启动写进 cast_config.json 的值 —— 两个设置按定案是不同口径。
     */
    public void StartDefault()
    {
        var first = LanAddresses.Get().FirstOrDefault()
            ?? throw new InvalidOperationException("没找到局域网网卡，请先连接网络");
        Start(first.Ip, first.Nic, CastOptions.LoadPort(), StartReason.Startup);
    }

    public void Start(string ip, string nic, int preferredPort, StartReason reason)
    {
        if (_running) return;
        if (!IPAddress.TryParse(ip, out var bindIp) || bindIp.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("网卡地址无效：" + ip);

        _ip = ip;
        NicName = nic;
        _udn = "uuid:pocketnas-cast-" + Convert.ToHexString(Encoding.UTF8.GetBytes(Environment.MachineName))
            .ToLowerInvariant();

        _cts = new CancellationTokenSource();
        TcpListener listener;
        try
        {
            listener = BindHttp(bindIp, preferredPort, reason);
        }
        catch
        {
            // 旧版在这里留下一个已创建却没 Dispose 的 CTS，异常路径下别再泄漏。
            _cts.Dispose();
            _cts = null;
            throw;
        }
        _http = listener;
        _port = ((IPEndPoint)_http.LocalEndpoint).Port;
        _running = true;
        _casting = false;

        // 只有真正跑起来才记住端口：点按钮、改网卡、异常路径都不污染上次的值。
        CastOptions.SavePort(_port);

        _ = Task.Run(() => HttpLoopAsync(_cts.Token));
        StartSsdp(_cts.Token);
        _ = Task.Run(() => NotifyLoopAsync(_cts.Token));
        LogService.Info("投屏接收", $"服务已启动：{DescriptionUrl}");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _casting = false;
        try { _cts?.Cancel(); } catch { }
        try { _http?.Stop(); } catch { }
        try { _ssdp?.Close(); } catch { }
        _http = null;
        _ssdp = null;
        _cts?.Dispose();
        _cts = null;
        LogService.Info("投屏接收", "服务已停止");
    }

    public void Dispose() => Stop();

    /** 只绑选中网卡的 IP，不再 0.0.0.0 全绑，与第 9 条「一个值两用」保持一致。 */
    private TcpListener BindHttp(IPAddress bindIp, int preferredPort, StartReason reason)
    {
        var index = 0;
        foreach (var p in PortCandidates(preferredPort))
        {
            try
            {
                var listener = new TcpListener(bindIp, p);
                listener.Start();
                if (index == 0)
                {
                    if (reason == StartReason.Startup)
                        SetNotice(preferredPort > 0
                            ? $"使用上次端口 {p}"
                            : $"首次启动，使用端口 {p}");
                }
                else if (preferredPort > 0)
                {
                    SetNotice($"上次的 {preferredPort} 被占用，已改用 {p}", LogLevel.Warn);
                }
                else
                {
                    SetNotice($"端口 {FirstPort} 被占用，已改用 {p}", LogLevel.Warn);
                }
                return listener;
            }
            catch (SocketException) { }
            index++;
        }
        var msg = $"{FirstPort}–{LastPort} 全部被占用，无法启动投屏接收";
        SetNotice(msg, LogLevel.Error);
        throw new InvalidOperationException(msg);
    }

    /** 上次端口优先；被占就从它往后顺延，顺完再回头补前面的，共 32 个备选。 */
    private static IEnumerable<int> PortCandidates(int preferredPort)
    {
        var range = Enumerable.Range(FirstPort, PortSlots);
        if (preferredPort <= 0)
        {
            foreach (var p in range) yield return p;
            yield break;
        }
        yield return preferredPort;
        foreach (var p in range.Where(p => p > preferredPort)) yield return p;
        foreach (var p in range.Where(p => p < preferredPort)) yield return p;
    }

    /** 「检测空闲端口并使用」按钮：找和当前端口不同的空闲端口，探测完立刻释放。 */
    public static int FindFreePort(int exclude)
    {
        foreach (var p in Enumerable.Range(FirstPort, PortSlots))
        {
            if (p == exclude) continue;
            TcpListener? probe = null;
            try
            {
                probe = new TcpListener(IPAddress.Any, p);
                probe.Start();
                return p;
            }
            catch (SocketException) { }
            finally { probe?.Stop(); }
        }
        return -1;
    }

    private async Task HttpLoopAsync(CancellationToken token)
    {
        var listener = _http!;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 8000;
            client.SendTimeout = 8000;
            var ns = client.GetStream();

            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 8192)
            {
                var n = ns.Read(one, 0, 1);
                if (n <= 0) return;
                header.Add(one[0]);
                var c = header.Count;
                if (c >= 4 && header[c - 4] == 13 && header[c - 3] == 10 &&
                    header[c - 2] == 13 && header[c - 1] == 10) break;
            }
            var lines = Encoding.UTF8.GetString(header.ToArray())
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var path = parts[1].Split('?')[0];

            var bodyLen = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[15..].Trim(), out var v)) bodyLen = v;
            }
            var body = new byte[Math.Min(bodyLen, 256 * 1024)];
            var read = 0;
            while (read < body.Length)
            {
                var n = ns.Read(body, read, body.Length - read);
                if (n <= 0) break;
                read += n;
            }
            var text = Encoding.UTF8.GetString(body, 0, read);
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "未知";

            if (method == "OPTIONS")
            {
                Reply(ns, 204, "text/plain", "");
                return;
            }
            if (method == "GET" && path == "/cast/desc.xml")
            {
                Reply(ns, 200, "text/xml; charset=utf-8", DescriptionXml());
                return;
            }
            if (method == "GET" && path == "/cast/ping")
            {
                Reply(ns, 200, "text/plain; charset=utf-8", "ok");
                return;
            }
            if (method == "POST" && path == "/cast")
            {
                CastRequest? req = null;
                try
                {
                    req = JsonSerializer.Deserialize<CastRequest>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { }
                if (req is null || !Valid(req.Url))
                {
                    Reply(ns, 400, "text/plain; charset=utf-8", "bad request");
                    return;
                }
                if (RequireConfirm && !Confirm(req, remote))
                {
                    Reply(ns, 403, "text/plain; charset=utf-8", "denied");
                    LogService.Warn("投屏接收", $"已拒绝来自 {remote} 的投送");
                    return;
                }
                _casting = true;
                CastRequested?.Invoke(req, remote);
                Reply(ns, 200, "application/json; charset=utf-8", "{\"ok\":true}");
                LogService.Success("投屏接收", $"来自 {remote}：{req.Title}");
                return;
            }
            if (method == "POST" && path == "/cast/stop")
            {
                _casting = false;
                StopRequested?.Invoke();
                Reply(ns, 200, "application/json; charset=utf-8", "{\"ok\":true}");
                LogService.Info("投屏接收", $"来自 {remote} 的停止请求");
                return;
            }
            Reply(ns, 404, "text/plain; charset=utf-8", "not found");
        }
        catch (Exception ex)
        {
            LogService.Warn("投屏接收", $"HTTP 连接异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    private static bool Valid(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        return !u.IsLoopback;
    }

    private static bool Confirm(CastRequest req, string remote)
    {
        var app = Application.Current;
        if (app is null) return true;
        return app.Dispatcher.Invoke(() =>
            MessageBox.Show($"来自 {remote} 的投屏请求：\n{req.Title}\n\n允许播放吗？",
                "投屏接收确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    }

    private static void Reply(NetworkStream ns, int code, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reason = code switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request",
            403 => "Forbidden", _ => "Not Found" };
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\n" +
            $"Content-Length: {bytes.Length}\r\nConnection: close\r\n" +
            "Access-Control-Allow-Origin: *\r\n\r\n");
        ns.Write(head);
        ns.Write(bytes);
        ns.Flush();
    }

    /** 手机端 SsdpDiscovery.describe() 靠 deviceType 里的 pocketnas 认出这是 PC。 */
    private string DescriptionXml() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>{StPc}</deviceType>
            <friendlyName>{XmlEscape(DeviceName)}</friendlyName>
            <UDN>{_udn}</UDN>
            <manufacturer>PocketNAS</manufacturer>
            <modelName>PocketNAS-PC Cast Receiver</modelName>
            <serviceList>
              <service>
                <serviceType>urn:pocketnas:service:Cast:1</serviceType>
                <controlURL>/cast</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private void StartSsdp(CancellationToken token)
    {
        try
        {
            var udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            // 多网卡机器（虚拟网卡/Hyper-V/雷电）必须指定物理 LAN 接口，否则组播走默认路由那块虚拟网卡。
            udp.JoinMulticastGroup(IPAddress.Parse(Group), IPAddress.Parse(_ip));
            _ssdp = udp;
            _ = Task.Run(() => SsdpLoop(udp, token));
        }
        catch (Exception ex)
        {
            LogService.Warn("投屏接收", $"SSDP 1900 监听不可用（{ex.GetType().Name}），改用主动 NOTIFY 广播");
        }
    }

    private void SsdpLoop(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var raw = Encoding.UTF8.GetString(udp.Receive(ref ep));
                if (!raw.StartsWith("M-SEARCH", StringComparison.Ordinal)) continue;
                var st = "";
                foreach (var line in raw.Split("\r\n"))
                {
                    if (line.StartsWith("ST:", StringComparison.OrdinalIgnoreCase)) st = line[3..].Trim();
                }
                if (st.Length > 0 && st != "ssdp:all" && st != StPc) continue;
                var resp = Encoding.UTF8.GetBytes(SsdpResponse(st));
                udp.Send(resp, resp.Length, ep);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex) { LogService.Warn("投屏接收", $"SSDP 异常：{ex.Message}"); }
        }
    }

    private string SsdpResponse(string st) =>
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=60\r\n" +
        "EXT:\r\n" +
        $"LOCATION: {DescriptionUrl}\r\n" +
        "SERVER: PocketNAS-PC/1.0 UPnP/1.0\r\n" +
        $"ST: {(st.Length > 0 ? st : StPc)}\r\n" +
        $"USN: {_udn}::{StPc}\r\n" +
        "X-PocketNAS-PC: 1\r\n" +
        "\r\n";

    /** 主动广播：1900 被 SSDPSRV 占了也照样能被手机发现；手机端 discover() 接受 NOTIFY。 */
    private async Task NotifyLoopAsync(CancellationToken token)
    {
        try
        {
            // 关键：绑定物理 LAN 网卡再发，否则 Windows 会按默认路由挑一块虚拟网卡，
            // 组播根本到不了手机所在的 192.168.3.x 网段。
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Parse(_ip), 0));
            sender.Ttl = 4;
            LogService.Info("投屏接收", $"SSDP NOTIFY 从 {_ip} 发出（组播 {Group}:{SsdpPort}）");
            var alive = Encoding.UTF8.GetBytes(Notify("ssdp:alive"));
            while (!token.IsCancellationRequested)
            {
                try { sender.Send(alive, alive.Length, Group, SsdpPort); } catch { }
                await Task.Delay(NotifyIntervalMs, token);
            }
            var bye = Encoding.UTF8.GetBytes(Notify("ssdp:byebye"));
            try { sender.Send(bye, bye.Length, Group, SsdpPort); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Warn("投屏接收", $"NOTIFY 异常：{ex.Message}"); }
    }

    private string Notify(string nts) =>
        "NOTIFY * HTTP/1.1\r\n" +
        $"HOST: {Group}:{SsdpPort}\r\n" +
        "CACHE-CONTROL: max-age=60\r\n" +
        $"LOCATION: {DescriptionUrl}\r\n" +
        $"NT: {StPc}\r\n" +
        $"NTS: {nts}\r\n" +
        "SERVER: PocketNAS-PC/1.0 UPnP/1.0\r\n" +
        $"USN: {_udn}::{StPc}\r\n" +
        "X-PocketNAS-PC: 1\r\n" +
        "\r\n";
}
