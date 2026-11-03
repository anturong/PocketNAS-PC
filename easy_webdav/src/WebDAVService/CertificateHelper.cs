using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

public static class CertificateHelper
{
    private static readonly Guid AppId = Guid.Parse("3A7E8F2C-D5B1-4A6E-9C83-2F1E5A7B4D8C");

    public static string? EnsureSelfSignedCert(string hostName, int httpsPort)
    {
        try
        {
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            // 查找是否已有此主机的有效证书
            var existingCerts = store.Certificates
                .Where(c => c.GetNameInfo(X509NameType.DnsName, false) == hostName
                         && c.NotAfter > DateTimeOffset.Now.AddDays(30))
                .ToList();

            X509Certificate2? cert;
            if (existingCerts.Count > 0)
            {
                cert = existingCerts[0];
                Log.Information("使用现有证书: {Subject}, 过期: {Expiry}", cert.Subject, cert.NotAfter);
            }
            else
            {
                Log.Information("创建自签名证书: CN={HostName}", hostName);
                cert = CreateSelfSignedCert(hostName);
                store.Add(cert);
                Log.Information("证书已安装到 LocalMachine\\My");
            }

            store.Close();
            var thumbprint = cert.Thumbprint;

            // 绑定证书到端口
            BindCertToPort(thumbprint, httpsPort);

            return thumbprint;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTPS 证书初始化失败");
            return null;
        }
    }

    public static void RemoveCertBinding(int httpsPort)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"http delete sslcert ipport=0.0.0.0:{httpsPort}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi)!;
            process.WaitForExit(5000);
            Log.Information("已删除端口 {Port} 的 SSL 证书绑定", httpsPort);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除 SSL 证书绑定失败");
        }
    }

    private static X509Certificate2 CreateSelfSignedCert(string hostName)
    {
        var subject = new X500DistinguishedName($"CN={hostName}");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // SAN 扩展 — 浏览器需要
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostName);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        // 基本约束
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        // 密钥用途
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        // 增强密钥用途 — 服务器认证
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var cert = request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(5));

        // 导出为 MachineKeySet 以便 netsh http add sslcert 能找到私钥
        var exported = cert.Export(X509ContentType.Pfx, "");
        return new X509Certificate2(exported, "",
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    private static void BindCertToPort(string thumbprint, int port)
    {
        // 先删除旧绑定
        RemoveCertBinding(port);

        var psi = new ProcessStartInfo("netsh",
            $"http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid={{{AppId}}}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);

        if (process.ExitCode == 0)
            Log.Information("SSL 证书绑定成功: 0.0.0.0:{Port}", port);
        else
            Log.Warning("SSL 证书绑定结果: {Output} {Error}", output, error);
    }
}
