using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PocketNAS.Shared;

/// <summary>一块网卡上的一个 IPv4 地址</summary>
public sealed record LanAddress(string Ip, string Nic, bool HasGateway, int Rank)
{
    /// <summary>下拉框统一显示为「IP · 网卡名」，两个模块保持一致</summary>
    public override string ToString() => $"{Ip}  ·  {Nic}";
}

/// <summary>
/// 局域网地址枚举：WebDAV 与 SSH 两个模块共用同一套筛选与排序规则。
/// 虚拟网卡（VMware/Hyper-V 等）通常没有默认网关，手机访问不到，
/// 因此按「有网关 &gt; 以太网/WLAN &gt; 其他」排序。
/// </summary>
public static class LanAddresses
{
    public static IReadOnlyList<LanAddress> Get()
    {
        var found = new List<LanAddress>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null &&
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));

                var rank = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet => 0,
                    NetworkInterfaceType.Wireless80211 => 1,
                    _ => 2,
                };

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    if (found.Any(f => f.Ip == ip)) continue;
                    found.Add(new LanAddress(ip, nic.Name, hasGateway, rank));
                }
            }
        }
        catch
        {
            // 枚举失败就返回已有结果，调用方自行处理空列表
        }

        return found
            .OrderByDescending(f => f.HasGateway)
            .ThenBy(f => f.Rank)
            .ThenBy(f => f.Nic)
            .ToList();
    }
}
