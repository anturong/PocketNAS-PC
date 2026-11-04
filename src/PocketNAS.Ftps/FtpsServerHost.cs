using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace FtpsServer;

/// <summary>
/// FTPS 服务器实例的生命周期封装，可被控制台或 WPF 外壳托管。
///
/// 注意：这是**进程内**的服务器对象，不是 Windows 服务。
/// 需要开机自启/无人登录运行时，需要外层再包一个服务宿主（参照 WebDAVService）。
/// </summary>
public sealed class FtpsServerHost : IAsyncDisposable
{
    private readonly FtpsOptions _options;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _explicitListener;
    private TcpListener? _implicitListener;
    private Task _explicitLoop = Task.CompletedTask;
    private Task _implicitLoop = Task.CompletedTask;
    private X509Certificate2? _cert;

    public FtpsServerHost(FtpsOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? (_ => { });
    }

    public FtpsOptions Options => _options;

    public bool IsRunning => _explicitListener != null || _implicitListener != null;

    public string ExplicitEndpoint => $"{_options.ListenIp}:{_options.ExplicitPort}";

    public string ImplicitEndpoint => $"{_options.ListenIp}:{_options.ImplicitPort}";

    public string PasvRange => $"{_options.PasvMin}-{_options.PasvMax}";

    /// <summary>启动两个监听端口。端口被占用等错误会直接抛出，由调用方提示用户。</summary>
    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        // 协议实现按单实例编写，配置装载进 Program 的静态字段
        Program.ApplyOptions(_options);
        Program.LogSink = _log;
        _cert = Program.LoadOrCreateCert();

        _explicitListener = Bind(_options.ListenIp, _options.ExplicitPort);
        try
        {
            _implicitListener = Bind(_options.ListenIp, _options.ImplicitPort);
        }
        catch
        {
            _explicitListener.Stop();
            _explicitListener = null;
            throw;
        }

        _explicitLoop = AcceptLoopAsync(_explicitListener, implicitTls: false);
        _implicitLoop = AcceptLoopAsync(_implicitListener, implicitTls: true);

        _log($"FTPS 已启动：显式 {_options.ExplicitPort} / 隐式 {_options.ImplicitPort}，根目录 {Program.RootDir}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _cts.Cancel();
        try { _explicitListener?.Stop(); } catch { }
        try { _implicitListener?.Stop(); } catch { }

        try { await Task.WhenAll(_explicitLoop, _implicitLoop).ConfigureAwait(false); }
        catch { }

        _explicitListener = null;
        _implicitListener = null;
        _log("FTPS 已停止");
    }

    /// <summary>控制台模式下等待服务器退出（外壳模式下不需要调用）</summary>
    public Task WaitForShutdownAsync() => Task.WhenAll(_explicitLoop, _implicitLoop);

    private static TcpListener Bind(string ip, int port)
    {
        var address = IPAddress.TryParse(ip, out var parsed) ? parsed : IPAddress.Any;
        var listener = new TcpListener(address, port);
        listener.Start();
        return listener;
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool implicitTls)
    {
        var cert = _cert!;
        var tag = implicitTls ? "隐式" : "显式";
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => Program.HandleClient(client, cert, _cts.Token, implicitTls), _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log($"[{tag}] 接受连接失败: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
