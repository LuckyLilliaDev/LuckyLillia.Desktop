using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace LuckyLilliaDesktop.Utils;

/// <summary>给实例分配 WebUI 端口用的本机端口探测.</summary>
public static class TcpPorts
{
    public static bool IsFree(int port) => IsFree(port, TcpListenerTable.GetListeners());

    // Windows 上 Node 的监听 socket 允许别的进程再绑同一个端口 (SO_REUSEADDR 语义), 试绑定会把被占的端口当成空闲,
    // 所以先查系统的 TCP 监听表 (IPv4 + IPv6, 任意地址). 没有监听表的平台只靠试绑定: 回环和通配地址都能独占绑定才算空闲.
    private static bool IsFree(int port, List<TcpListenerTable.Listener>? listeners)
    {
        if (port is <= 0 or > 65535) return false;
        if (listeners != null && listeners.Any(l => l.Port == port)) return false;
        try
        {
            using var any = new TcpListener(IPAddress.Any, port);
            any.ExclusiveAddressUse = true;
            any.Start();
            any.Stop();
            using var loopback = new TcpListener(IPAddress.Loopback, port);
            loopback.ExclusiveAddressUse = true;
            loopback.Start();
            loopback.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从首选端口起往上找第一个空闲且不在 <paramref name="exclude"/> 里的端口; 范围内都不行就让系统分配一个; 都失败返回 null.
    /// </summary>
    public static int? FindFree(int preferred, ISet<int>? exclude = null, int range = 100)
    {
        var listeners = TcpListenerTable.GetListeners();
        var start = preferred is > 0 and <= 65535 ? preferred : LLBotWebUi.DefaultPort;
        for (var port = start; port < Math.Min(start + range, 65536); port++)
        {
            if (exclude != null && exclude.Contains(port)) continue;
            if (IsFree(port, listeners)) return port;
        }
        // Hyper-V / WSL / Docker 会按 100 个一段保留端口 (netsh int ipv4 show excludedportrange), 一段就能盖住整个搜索范围
        // (实际遇到: 3080 被占, 3081-3180 被保留). 系统分配的端口不会落在保留段里
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = SystemAssignedPort();
            if (port is int p && (exclude == null || !exclude.Contains(p)) && IsFree(p, listeners)) return p;
        }
        return null;
    }

    private static int? SystemAssignedPort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
