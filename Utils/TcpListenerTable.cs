using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 本机 TCP 监听端口及其归属进程 (Windows, iphlpapi GetExtendedTcpTable). 用来核对 WebUI 端口是不是真的由我们拉起的那个
/// LLBot 进程在监听: 旧版 LLBot 不认 --webui-port, 端口被占时会自己换一个, 只做连通探测会连到别的服务上.
/// 非 Windows 或读取失败返回 null, 调用方退回 HTTP 特征探测.
/// </summary>
internal static partial class TcpListenerTable
{
    public readonly record struct Listener(int Port, int Pid);

    private const uint AF_INET = 2;
    private const uint AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint MIB_TCP_STATE_LISTEN = 2;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const int MaxReadAttempts = 3;

    internal delegate uint TableReader(nint buffer, ref uint size, uint family);

    public static List<Listener>? GetListeners()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetListeners(ReadTable);
    }

    internal static List<Listener>? GetListeners(TableReader reader)
    {
        var list = new List<Listener>();
        try
        {
            // 失败不能伪装成空表或部分成功, 否则调用方会把缺失的端口当成没有监听, 跳过 HTTP 兜底.
            if (!Fill(list, AF_INET, reader) || !Fill(list, AF_INET6, reader)) return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        return list;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, uint ulAf, int tableClass, uint reserved);

    private static uint ReadTable(nint buffer, ref uint size, uint family) =>
        GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);

    // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid (6 x DWORD)
    // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid
    // 两种表都以 DWORD dwNumEntries 开头. 端口字段是网络字节序, 只用低 16 位.
    private static unsafe bool Fill(List<Listener> list, uint family, TableReader reader)
    {
        uint size = 0;
        var rc = reader(0, ref size, family);
        if (rc != ERROR_INSUFFICIENT_BUFFER) return false;

        // 查询所需大小和读取之间可能新增监听, 按 API 返回的新大小重试, 但不无限等.
        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            if (size < sizeof(uint) || size > int.MaxValue) return false;
            var allocatedSize = size;
            var buffer = Marshal.AllocHGlobal((int)allocatedSize);
            try
            {
                rc = reader(buffer, ref size, family);
                if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                if (rc != 0) return false;

                var count = *(uint*)buffer;
                var rows = (byte*)buffer + sizeof(uint);
                var rowSize = family == AF_INET ? 6 * sizeof(uint) : 56;
                if (count > (allocatedSize - sizeof(uint)) / rowSize) return false;
                for (uint i = 0; i < count; i++)
                {
                    if (family == AF_INET)
                    {
                        var row = (uint*)(rows + i * rowSize);
                        if (row[0] != MIB_TCP_STATE_LISTEN) continue;
                        list.Add(new Listener(NetworkPort(row[2]), (int)row[5]));
                    }
                    else
                    {
                        var row = rows + i * rowSize;
                        if (*(uint*)(row + 48) != MIB_TCP_STATE_LISTEN) continue;
                        list.Add(new Listener(NetworkPort(*(uint*)(row + 20)), (int)*(uint*)(row + 52)));
                    }
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return false;
    }

    private static int NetworkPort(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
}
