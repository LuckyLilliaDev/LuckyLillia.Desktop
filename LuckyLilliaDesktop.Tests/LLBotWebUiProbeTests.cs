using System.Net;
using System.Net.Sockets;
using System.Text;
using LuckyLilliaDesktop.Utils;
using Xunit;

namespace LuckyLilliaDesktop.Tests;

[CollectionDefinition("WebUI HTTP probe", DisableParallelization = true)]
public class WebUiProbeCollection { }

[Collection("WebUI HTTP probe")]
public class LLBotWebUiProbeTests
{
    [Fact]
    public async Task A_failed_Windows_table_read_falls_back_to_the_reachable_WebUI()
    {
        uint FailedRead(nint buffer, ref uint size, uint family) => 5;
        var listeners = TcpListenerTable.GetListeners(FailedRead);
        await using var server = new ResponseServer("403 Forbidden", "X-Webui-Auth: required\r\n");
        var ready = await LLBotWebUi.FindReadyPortAsync(server.Port, 12345, listeners,
            port => LLBotWebUi.IsWebUiAsync("127.0.0.1", port));
        Assert.Equal(server.Port, ready);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_port_owned_by_the_instance_is_ready_without_HTTP()
    {
        var listeners = new[] { new TcpListenerTable.Listener(3080, 12345) };
        var ready = await LLBotWebUi.FindReadyPortAsync(3080, 12345, listeners,
            _ => throw new InvalidOperationException("HTTP probe should not be needed"));
        Assert.Equal(3080, ready);
    }

    [Fact]
    public async Task Does_not_accept_another_process_on_the_requested_port()
    {
        var listeners = new[] { new TcpListenerTable.Listener(3080, 99999) };
        var ready = await LLBotWebUi.FindReadyPortAsync(3080, 12345, listeners,
            _ => throw new InvalidOperationException("Must not probe the other process"));
        Assert.Null(ready);
    }

    [Fact]
    public async Task A_successful_empty_table_keeps_waiting_for_the_instance_to_listen()
    {
        var ready = await LLBotWebUi.FindReadyPortAsync(3080, 12345, Array.Empty<TcpListenerTable.Listener>(),
            _ => throw new InvalidOperationException("An empty table is not a failed read"));
        Assert.Null(ready);
    }

    [Fact]
    public async Task Detects_a_different_port_only_among_the_instances_listeners()
    {
        var listeners = new[] { new TcpListenerTable.Listener(3080, 99999), new TcpListenerTable.Listener(3081, 12345) };
        var probed = new List<int>();
        var ready = await LLBotWebUi.FindReadyPortAsync(3080, 12345, listeners, port =>
        {
            probed.Add(port);
            return Task.FromResult(true);
        });
        Assert.Equal(3081, ready);
        Assert.Equal(new[] { 3081 }, probed);
    }

    [Theory]
    [InlineData("403 Forbidden", "X-Webui-Auth: required\r\n", true)]
    [InlineData("401 Unauthorized", "X-Webui-Auth: invalid\r\n", true)]
    [InlineData("200 OK", "", false)]
    [InlineData("403 Forbidden", "", false)]
    public async Task Recognizes_the_WebUI_auth_header_instead_of_plain_HTTP_success(string status, string headers, bool expected)
    {
        await using var server = new ResponseServer(status, headers);
        Assert.Equal(expected, await LLBotWebUi.IsWebUiAsync("127.0.0.1", server.Port));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Does_not_follow_another_services_redirect_to_a_WebUI()
    {
        await using var target = new ResponseServer("403 Forbidden", "X-Webui-Auth: required\r\n");
        await using var redirect = new ResponseServer("302 Found", $"Location: http://127.0.0.1:{target.Port}/api/login-info\r\n");
        Assert.False(await LLBotWebUi.IsWebUiAsync("127.0.0.1", redirect.Port));
        Assert.Equal(0, target.Requests);
    }

    [Fact]
    public async Task Reaches_the_local_WebUI_even_when_the_system_proxy_cannot_connect()
    {
        var previousProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new UnreachableProxy();
        try
        {
            await using var server = new ResponseServer("403 Forbidden", "X-Webui-Auth: required\r\n");
            Assert.True(await LLBotWebUi.IsWebUiAsync("127.0.0.1", server.Port));
            Assert.Equal(1, server.Requests);
        }
        finally
        {
            HttpClient.DefaultProxy = previousProxy;
        }
    }

    private sealed class UnreachableProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => new("http://127.0.0.1:1");
        public bool IsBypassed(Uri host) => false;
    }

    private sealed class ResponseServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        private readonly Task serving;
        private readonly byte[] response;
        private int requests;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Requests => Volatile.Read(ref requests);

        public ResponseServer(string status, string headers)
        {
            response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Content-Length: 0\r\nConnection: close\r\n\r\n");
            listener.Start();
            serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    Interlocked.Increment(ref requests);
                    await stream.WriteAsync(response, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving;
            stop.Dispose();
        }
    }
}
