using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 本机 TCP 监听端口及其归属进程 (Windows, iphlpapi GetExtendedTcpTable). 用来核对 WebUI 端口是不是真的由我们拉起的那个
/// LLBot 进程在监听: 旧版 LLBot 不认 --webui-port, 端口被占时会自己换一个, 只做连通探测会连到别的服务上.
/// 非 Windows 返回 null, 调用方退回连通探测.
/// </summary>
internal static partial class TcpListenerTable
{
    public readonly record struct Listener(int Port, int Pid);

    private const uint AF_INET = 2;
    private const uint AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint MIB_TCP_STATE_LISTEN = 2;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public static List<Listener>? GetListeners()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var list = new List<Listener>();
        try
        {
            Fill(list, AF_INET);
            Fill(list, AF_INET6);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        return list;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, uint ulAf, int tableClass, uint reserved);

    // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid (6 x DWORD)
    // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid
    // 两种表都以 DWORD dwNumEntries 开头. 端口字段是网络字节序, 只用低 16 位.
    private static unsafe void Fill(List<Listener> list, uint family)
    {
        uint size = 0;
        var rc = GetExtendedTcpTable(0, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER || size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (rc != 0) return;

            var count = *(uint*)buffer;
            var rows = (byte*)buffer + sizeof(uint);
            if (family == AF_INET)
            {
                const int rowSize = 6 * sizeof(uint);
                for (uint i = 0; i < count; i++)
                {
                    var row = (uint*)(rows + i * rowSize);
                    if (row[0] != MIB_TCP_STATE_LISTEN) continue;
                    list.Add(new Listener(NetworkPort(row[2]), (int)row[5]));
                }
            }
            else
            {
                const int rowSize = 56;
                for (uint i = 0; i < count; i++)
                {
                    var row = rows + i * rowSize;
                    if (*(uint*)(row + 48) != MIB_TCP_STATE_LISTEN) continue;
                    list.Add(new Listener(NetworkPort(*(uint*)(row + 20)), (int)*(uint*)(row + 52)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int NetworkPort(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
}
