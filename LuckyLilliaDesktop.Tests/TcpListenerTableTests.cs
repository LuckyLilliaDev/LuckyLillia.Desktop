using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LuckyLilliaDesktop.Utils;
using Xunit;

namespace LuckyLilliaDesktop.Tests;

public class TcpListenerTableTests
{
    private readonly record struct Row(int Port, int Pid, int State = 2);

    [Theory]
    [InlineData(2u, false)]
    [InlineData(2u, true)]
    [InlineData(23u, false)]
    [InlineData(23u, true)]
    public void Failed_read_returns_null_instead_of_empty_or_partial_table(uint failedFamily, bool afterAllocation)
    {
        uint Read(nint buffer, ref uint size, uint family)
        {
            if (family == failedFamily && (buffer != 0) == afterAllocation) return 5;
            return CopyRows(buffer, ref size, family, new Row(3080, 12345));
        }

        Assert.Null(TcpListenerTable.GetListeners(Read));
    }

    [Fact]
    public void Successful_empty_tables_are_distinct_from_unavailable_tables()
    {
        uint Read(nint buffer, ref uint size, uint family) => CopyRows(buffer, ref size, family);

        var listeners = TcpListenerTable.GetListeners(Read);

        Assert.NotNull(listeners);
        Assert.Empty(listeners);
    }

    [Fact]
    public void Reads_ipv4_and_ipv6_ports_and_owners_and_filters_non_listeners()
    {
        uint Read(nint buffer, ref uint size, uint family) => family == 2
            ? CopyRows(buffer, ref size, family, new Row(3080, 12345), new Row(5000, 7, 5))
            : CopyRows(buffer, ref size, family, new Row(65535, 67890), new Row(8080, 8, 5));

        Assert.Equal(new[]
        {
            new TcpListenerTable.Listener(3080, 12345),
            new TcpListenerTable.Listener(65535, 67890),
        }, TcpListenerTable.GetListeners(Read));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(23u)]
    public void Retries_when_the_table_grows_between_size_query_and_read(uint growingFamily)
    {
        var reads = 0;
        uint Read(nint buffer, ref uint size, uint family)
        {
            if (family == growingFamily)
            {
                if (buffer == 0) { size = 4; return 122; }
                reads++;
            }
            return CopyRows(buffer, ref size, family, new Row(3080, 12345));
        }

        var listeners = TcpListenerTable.GetListeners(Read);

        Assert.NotNull(listeners);
        Assert.Equal(2, listeners.Count);
        Assert.All(listeners, listener => Assert.Equal(new TcpListenerTable.Listener(3080, 12345), listener));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Continuously_growing_table_stops_retrying_and_reports_unavailable()
    {
        var reads = 0;
        uint Read(nint buffer, ref uint size, uint family)
        {
            if (buffer != 0) reads++;
            size += 24;
            return 122;
        }

        Assert.Null(TcpListenerTable.GetListeners(Read));
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_native_api_reports_unavailable(bool missingEntryPoint)
    {
        uint Read(nint buffer, ref uint size, uint family) => throw (missingEntryPoint
            ? new EntryPointNotFoundException()
            : new DllNotFoundException());

        Assert.Null(TcpListenerTable.GetListeners(Read));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(uint.MaxValue)]
    public void Invalid_buffer_size_reports_unavailable(uint requiredSize)
    {
        uint Read(nint buffer, ref uint size, uint family) { size = requiredSize; return 122; }

        Assert.Null(TcpListenerTable.GetListeners(Read));
    }

    [Fact]
    public void Row_count_outside_allocated_buffer_reports_unavailable()
    {
        uint Read(nint buffer, ref uint size, uint family)
        {
            if (buffer == 0) { size = 4; return 122; }
            Marshal.WriteInt32(buffer, 1);
            return 0;
        }

        Assert.Null(TcpListenerTable.GetListeners(Read));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_loopback_listener_belongs_to_this_process(bool ipv6)
    {
        if (!OperatingSystem.IsWindows() || (ipv6 && !Socket.OSSupportsIPv6)) return;
        using var listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var listeners = TcpListenerTable.GetListeners();

        Assert.NotNull(listeners);
        Assert.Contains(new TcpListenerTable.Listener(port, Environment.ProcessId), listeners);
    }

    private static uint CopyRows(nint buffer, ref uint size, uint family, params Row[] rows)
    {
        var rowSize = family == 2 ? 24 : 56;
        var required = (uint)(4 + rowSize * rows.Length);
        if (buffer == 0 || size < required) { size = required; return 122; }
        size = required;
        Marshal.WriteInt32(buffer, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = buffer + 4 + rowSize * i;
            var networkPort = ((rows[i].Port & 255) << 8) | (rows[i].Port >> 8);
            Marshal.WriteInt32(row, family == 2 ? 0 : 48, rows[i].State);
            Marshal.WriteInt32(row, family == 2 ? 8 : 20, networkPort);
            Marshal.WriteInt32(row, family == 2 ? 20 : 52, rows[i].Pid);
        }
        return 0;
    }
}
