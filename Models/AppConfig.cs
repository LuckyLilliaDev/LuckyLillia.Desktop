using System.Collections.Generic;
using System.Text.Json.Serialization;
using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop.Models;

/// <summary>
/// app_settings.json. 只放 Desktop 自己的设置; LLBot 的配置 (登录方式 / QQ 路径 / 协议 / WebUI 端口...) 全在 LLBot WebUI 里改.
/// 字段归属与写回约定见 doc/config-persistence.md.
/// </summary>
public class AppConfig
{
    [JsonPropertyName("llbot_path")]
    public string LLBotPath { get; set; } = "bin/llbot/llbot.js";

    [JsonPropertyName("node_path")]
    public string NodePath { get; set; } = Constants.DefaultPaths.NodeExe;

    // 程序启动时自动拉起的 LLBot 实例名. 实例目录在 <LLBot>/data/instances/, 见 doc/llbot-webui.md
    [JsonPropertyName("auto_start_instances")]
    public List<string> AutoStartInstances { get; set; } = [LLBotWebUi.DefaultInstance];

    // 开发模式, 没有命令行开关, 见 DevMode
    [JsonPropertyName("dev")]
    public bool Dev { get; set; } = false;

    [JsonPropertyName("minimize_to_tray_on_start")]
    public bool MinimizeToTrayOnStart { get; set; } = false;

    [JsonPropertyName("startup_command_enabled")]
    public bool StartupCommandEnabled { get; set; } = false;

    [JsonPropertyName("startup_command")]
    public string StartupCommand { get; set; } = string.Empty;

    // HTTP 代理: 非空时以 HTTP_PROXY/HTTPS_PROXY 环境变量传给 LLBot
    [JsonPropertyName("http_proxy")]
    public string HttpProxy { get; set; } = string.Empty;

    // 服务器线路: "overseas"=国外(默认), "china"=国内. 选国内时给 LLBot 传 --cdn china.
    [JsonPropertyName("server_region")]
    public string ServerRegion { get; set; } = "overseas";

    // 关闭行为, 三态语义见 doc/config-persistence.md
    [JsonPropertyName("close_to_tray")]
    public bool? CloseToTray { get; set; } = null;

    // 窗口设置
    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "system";

    [JsonPropertyName("window_width")]
    public int WindowWidth { get; set; } = 1200;

    [JsonPropertyName("window_height")]
    public int WindowHeight { get; set; } = 800;

    [JsonPropertyName("window_left")]
    public int? WindowLeft { get; set; } = null;

    [JsonPropertyName("window_top")]
    public int? WindowTop { get; set; } = null;

    public static AppConfig Default => new();
}
