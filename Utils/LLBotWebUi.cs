using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Utils;

/// <summary>LLBot 实例 settings.json 里的 webui 段.</summary>
public sealed record WebUiEndpoint(bool Enabled, string Host, int Port)
{
    /// <summary>本机连接用的主机名 (不带方括号), 给 TCP 探测用.</summary>
    public string ConnectHost => LLBotWebUi.ConnectHost(Host);

    /// <summary>Desktop 打开的地址: 监听在通配地址时本机回环即可.</summary>
    public string Url => LLBotWebUi.Url(ConnectHost, Port);
}

/// <summary>
/// LLBot 实例与 WebUI 地址. LLBot 的配置按实例分目录 (data/instances/&lt;实例名&gt;/, 见 LLBot docs/config-storage.md),
/// 每个实例的 webui.host / webui.port 在它的 settings.json 里; Desktop 启动实例时把分配好的端口以 --webui-port 传入.
/// 实例名规则与 LLBot src/common/instance.ts 一致.
/// </summary>
public static partial class LLBotWebUi
{
    public const string DefaultInstance = "default";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 3080;

    public static readonly WebUiEndpoint DefaultEndpoint = new(true, DefaultHost, DefaultPort);

    // Probe the local instance directly. System proxies, redirects and stored cookies can otherwise
    // hide the authentication header or make another service appear to be this instance.
    private static readonly HttpClient ProbeClient = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceNamePattern();

    public static bool IsValidInstanceName(string? name) => !string.IsNullOrEmpty(name) && InstanceNamePattern().IsMatch(name);

    /// <summary>配置里的实例名: 空白 -> default; 不合法时返回 null (LLBot 收到不合法的名字会直接退出).</summary>
    public static string? ResolveInstanceName(string? configured)
    {
        var name = configured?.Trim();
        if (string.IsNullOrEmpty(name)) return DefaultInstance;
        return IsValidInstanceName(name) ? name : null;
    }

    /// <summary>LLBot 的 DATA_DIR 是 path.resolve('data'), Desktop 启动它时工作目录就是脚本目录.</summary>
    public static string DataDir(string llbotScriptPath)
    {
        var llbotDir = Path.GetDirectoryName(Path.GetFullPath(llbotScriptPath)) ?? ".";
        return Path.Combine(llbotDir, "data");
    }

    public static string InstancesDir(string llbotScriptPath) => Path.Combine(DataDir(llbotScriptPath), "instances");

    public static string InstanceDir(string llbotScriptPath, string instance) => Path.Combine(InstancesDir(llbotScriptPath), instance);

    public static string SettingsPath(string llbotScriptPath, string instance) => Path.Combine(InstanceDir(llbotScriptPath, instance), "settings.json");

    /// <summary>实例锁的提示文件 (LLBot src/main/instanceLock.ts), 删实例时一并清掉.</summary>
    public static string LockHolderPath(string llbotScriptPath, string instance) => Path.Combine(DataDir(llbotScriptPath), "state", "instances", $"{instance}.json");

    /// <summary>data/instances 下的实例目录名 (只认合法名字), default 排最前, 其余按名字排序. 目录不存在时为空.</summary>
    public static List<string> ListInstances(string llbotScriptPath)
    {
        var result = new List<string>();
        var dir = InstancesDir(llbotScriptPath);
        if (!Directory.Exists(dir)) return result;
        try
        {
            foreach (var path in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(path);
                if (IsValidInstanceName(name)) result.Add(name);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        result.Sort((a, b) => a == DefaultInstance ? -1 : b == DefaultInstance ? 1 : string.CompareOrdinal(a, b));
        return result;
    }

    /// <summary>解析 settings.json 的 webui 段; 文件缺失 / 内容坏掉 / 字段缺失都退回 LLBot 默认值.</summary>
    public static WebUiEndpoint ParseEndpoint(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return DefaultEndpoint;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("webui", out var webui)
                || webui.ValueKind != JsonValueKind.Object)
            {
                return DefaultEndpoint;
            }

            var enabled = !(webui.TryGetProperty("enable", out var enable) && enable.ValueKind == JsonValueKind.False);
            var host = webui.TryGetProperty("host", out var hostElement) && hostElement.ValueKind == JsonValueKind.String
                ? hostElement.GetString() ?? DefaultHost
                : DefaultHost;
            var port = webui.TryGetProperty("port", out var portElement)
                       && portElement.ValueKind == JsonValueKind.Number
                       && portElement.TryGetInt32(out var parsedPort)
                       && parsedPort is > 0 and <= 65535
                ? parsedPort
                : DefaultPort;
            return new WebUiEndpoint(enabled, host, port);
        }
        catch (JsonException)
        {
            return DefaultEndpoint;
        }
    }

    public static WebUiEndpoint ReadEndpoint(string llbotScriptPath, string instance)
    {
        var path = SettingsPath(llbotScriptPath, instance);
        if (!File.Exists(path)) return DefaultEndpoint;
        try { return ParseEndpoint(File.ReadAllText(path)); }
        catch (IOException) { return DefaultEndpoint; }
        catch (UnauthorizedAccessException) { return DefaultEndpoint; }
    }

    /// <summary>监听地址 -> 本机连接地址: 通配 / 空 -> 回环; IPv6 字面量去掉方括号.</summary>
    public static string ConnectHost(string? host)
    {
        var value = host?.Trim() ?? string.Empty;
        if (value.Length == 0 || value is "0.0.0.0" or "::" or "[::]" or "*") return DefaultHost;
        return value.Trim('[', ']');
    }

    public static string Url(string connectHost, int port) =>
        connectHost.Contains(':') ? $"http://[{connectHost}]:{port}" : $"http://{connectHost}:{port}";

    /// <summary>优先匹配实例 PID; 端口表不可用时才用本机 HTTP 特征检测.</summary>
    internal static async Task<int?> FindReadyPortAsync(int port, int? pid,
        IReadOnlyList<TcpListenerTable.Listener>? listeners, Func<int, Task<bool>> isWebUi)
    {
        if (listeners != null && pid is int ownPid)
        {
            var owned = listeners.Where(l => l.Pid == ownPid).Select(l => l.Port).Distinct().ToList();
            if (owned.Contains(port)) return port;
            foreach (var candidate in owned)
            {
                if (await isWebUi(candidate)) return candidate;
            }
            return null;
        }
        return await isWebUi(port) ? port : null;
    }

    /// <summary>
    /// 这个端口上跑的是不是 LLBot 的 WebUI: /api/* 一律先过它的登录中间件, 拒绝时带 X-Webui-Auth 头 (BE/auth.ts),
    /// 别的服务不会有这个头. 连不上 / 超时 / 没有这个头都算不是.
    /// </summary>
    public static async Task<bool> IsWebUiAsync(string connectHost, int port, CancellationToken ct = default)
    {
        try
        {
            using var response = await ProbeClient.GetAsync($"{Url(connectHost, port)}/api/login-info", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Headers.Contains("x-webui-auth");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}
