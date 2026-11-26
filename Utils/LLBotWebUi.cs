using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Utils;

/// <summary>LLBot 统一网关 (或旧版 default 实例) 的 WebUI 监听地址.</summary>
public sealed record WebUiEndpoint(bool Enabled, string Host, int Port)
{
    /// <summary>本机连接用的主机名 (不带方括号), 给 TCP 探测用.</summary>
    public string ConnectHost => LLBotWebUi.ConnectHost(Host);

    /// <summary>Desktop 打开的地址: 监听在通配地址时本机回环即可.</summary>
    public string Url => LLBotWebUi.Url(ConnectHost, Port);
}

/// <summary>
/// LLBot 统一网关的 WebUI 地址、免登录 cookie 与开发版配置迁移.
/// LLBot 的 DATA_DIR 是 path.resolve('data'), 统一网关配置在 data/manager.json, 统一密码在 data/webui_token.txt.
/// </summary>
public static partial class LLBotWebUi
{
    public const string DefaultInstance = "default";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 3080;

    public static readonly WebUiEndpoint DefaultEndpoint = new(true, DefaultHost, DefaultPort);

    // Probe the local gateway directly. System proxies, redirects and stored cookies can otherwise
    // hide the authentication header or make another service appear to be LLBot.
    private static readonly HttpClient ProbeClient = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceNamePattern();

    public static bool IsValidInstanceName(string? name) => !string.IsNullOrEmpty(name) && InstanceNamePattern().IsMatch(name);

    /// <summary>LLBot 的 DATA_DIR 是 path.resolve('data'), Desktop 启动它时工作目录就是脚本目录.</summary>
    public static string DataDir(string llbotScriptPath)
    {
        var llbotDir = Path.GetDirectoryName(Path.GetFullPath(llbotScriptPath)) ?? ".";
        return Path.Combine(llbotDir, "data");
    }

    public static string ManagerConfigPath(string llbotScriptPath) => Path.Combine(DataDir(llbotScriptPath), "manager.json");

    /// <summary>统一 WebUI 密码文件 (明文, data/webui_token.txt).</summary>
    public static string WebUiTokenPath(string llbotScriptPath) => Path.Combine(DataDir(llbotScriptPath), "webui_token.txt");

    /// <summary>旧版单实例默认密码路径 (尚未迁移或跑旧版 LLBot 时的兜底).</summary>
    public static string LegacyDefaultTokenPath(string llbotScriptPath) => Path.Combine(DataDir(llbotScriptPath), "instances", DefaultInstance, "webui_token.txt");

    public static string LegacyDefaultSettingsPath(string llbotScriptPath) => Path.Combine(DataDir(llbotScriptPath), "instances", DefaultInstance, "settings.json");

    /// <summary>
    /// 内嵌 WebView 免登录用的 cookie: WebUI 前端把 sha256(密码) 的小写 hex 存在 <c>webui_token_&lt;端口&gt;</c>
    /// (FE/utils/api.ts tokenCookieName / setTokenStorage), 网关拿它和 sha256(data/webui_token.txt 去首尾空白) 比.
    /// 优先读统一密码 data/webui_token.txt, 不存在时兜底读旧版 data/instances/default/webui_token.txt.
    /// 还没设密码 (文件不存在 / 为空) 或读不了时返回 null, 让 WebUI 照常让用户设密码 / 输密码.
    /// </summary>
    public static (string Name, string Value)? LoginCookie(string llbotScriptPath, int port)
    {
        var password = TryReadTrimmedFile(WebUiTokenPath(llbotScriptPath))
            ?? TryReadTrimmedFile(LegacyDefaultTokenPath(llbotScriptPath));
        if (string.IsNullOrEmpty(password)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return ($"webui_token_{port}", Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string? TryReadTrimmedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>解析 data/manager.json 的 gateway 段; 缺失或损坏时返回 null.</summary>
    public static WebUiEndpoint? ParseManagerGateway(string? managerJson)
    {
        if (string.IsNullOrWhiteSpace(managerJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(managerJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("gateway", out var gateway)
                || gateway.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var host = gateway.TryGetProperty("host", out var hostElement) && hostElement.ValueKind == JsonValueKind.String
                ? hostElement.GetString() ?? DefaultHost
                : DefaultHost;
            var port = gateway.TryGetProperty("port", out var portElement)
                       && portElement.ValueKind == JsonValueKind.Number
                       && portElement.TryGetInt32(out var parsedPort)
                       && parsedPort is > 0 and <= 65535
                ? parsedPort
                : DefaultPort;
            return new WebUiEndpoint(true, host, port);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析旧版 settings.json 的 webui 段; 文件缺失 / 内容坏掉 / 字段缺失都退回 LLBot 默认值.</summary>
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

    /// <summary>
    /// 读取统一网关的监听地址: 优先 data/manager.json; 还没有 manager.json 时沿用 default 实例 settings.json 的 webui 段
    /// (与 LLBot Registry.legacyGateway 一致); 否则回落 127.0.0.1:3080.
    /// </summary>
    public static WebUiEndpoint ReadGatewayEndpoint(string llbotScriptPath)
    {
        if (string.IsNullOrWhiteSpace(llbotScriptPath)) return DefaultEndpoint;
        try
        {
            var managerPath = ManagerConfigPath(llbotScriptPath);
            if (File.Exists(managerPath))
            {
                var parsed = ParseManagerGateway(File.ReadAllText(managerPath));
                if (parsed != null) return parsed;
            }
            var legacyPath = LegacyDefaultSettingsPath(llbotScriptPath);
            if (File.Exists(legacyPath))
            {
                var legacy = ParseEndpoint(File.ReadAllText(legacyPath));
                // 管理器模式下网关始终启用, 不受旧 settings.json webui.enable 影响
                return legacy with { Enabled = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // fall through to defaults
        }
        return DefaultEndpoint;
    }

    /// <summary>
    /// 一次性迁移开发版 Desktop 的 auto_start_instances 到 LLBot 的 data/manager.json.
    /// 仅当 data/manager.json 尚不存在且列表包含非默认自启配置时写入, 不覆盖已有的 manager.json.
    /// </summary>
    public static bool MigrateAutoStartToManagerJson(string llbotScriptPath, IReadOnlyList<string>? autoStartInstances)
    {
        if (string.IsNullOrWhiteSpace(llbotScriptPath) || autoStartInstances == null) return false;
        var valid = autoStartInstances
            .Select(n => n?.Trim())
            .Where(IsValidInstanceName)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();
        // 和管理器默认行为 (仅 default 自启) 完全一致时不必提前写 manager.json, 交给 LLBot 首次启动生成即可
        if (valid.Count == 1 && valid[0] == DefaultInstance) return false;

        try
        {
            var managerPath = ManagerConfigPath(llbotScriptPath);
            if (File.Exists(managerPath)) return false;

            var gateway = ReadGatewayEndpoint(llbotScriptPath);
            var dataDir = DataDir(llbotScriptPath);
            Directory.CreateDirectory(dataDir);

            // 使用 Utf8JsonWriter, NativeAOT 下零反射依赖
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("gateway");
                writer.WriteString("host", gateway.Host);
                writer.WriteNumber("port", gateway.Port);
                writer.WriteEndObject();

                writer.WriteStartObject("instances");
                writer.WriteStartObject(DefaultInstance);
                writer.WriteBoolean("autostart", valid.Contains(DefaultInstance));
                writer.WriteEndObject();
                foreach (var name in valid)
                {
                    if (name == DefaultInstance) continue;
                    writer.WriteStartObject(name);
                    writer.WriteBoolean("autostart", true);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            var temp = $"{managerPath}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            try
            {
                File.Move(temp, managerPath, overwrite: false);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
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

    /// <summary>
    /// 优先匹配 LLBot 的进程树 (<paramref name="pids"/>: llbot.js + llbot-manager.js + 子进程);
    /// 端口表不可用时才用本机 HTTP 特征检测. 子实例内部端口不带 X-Webui-Auth 头, 不会被误认成网关.
    /// </summary>
    internal static async Task<int?> FindReadyPortAsync(int port, IReadOnlySet<int>? pids,
        IReadOnlyList<TcpListenerTable.Listener>? listeners, Func<int, Task<bool>> isWebUi)
    {
        if (listeners != null && pids is { Count: > 0 })
        {
            var owned = listeners.Where(l => pids.Contains(l.Pid)).Select(l => l.Port).Distinct().ToList();
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
    /// 这个端口上跑的是不是 LLBot 的统一 WebUI 网关: /api/* 一律先过网关登录中间件, 拒绝时带 X-Webui-Auth 头,
    /// 实例内部端口和别的服务都不会有这个头. 连不上 / 超时 / 没有这个头都算不是.
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
