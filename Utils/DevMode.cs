using System.IO;
using System.Text.Json;
using LuckyLilliaDesktop.Models;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 开发模式。只由 app_settings.json 的 <c>dev</c> 开启, 没有对应的命令行开关。
///
/// 作用: 启动 LLBot 时透传 <c>--dev</c> (LLBot 据此加载开发版原生组件、读 data/auth_token.dev.txt 并连本地 manager)。
/// Desktop 自己不再碰 auth token (在 LLBot 的 WebUI 里录入); 系统时间、版本门槛等启动检查不受影响。
/// </summary>
internal static class DevMode
{
    /// <summary>透传给 LLBot 的参数。Desktop 自己不解析命令行。</summary>
    internal const string Argument = "--dev";

    private const string ConfigPath = "app_settings.json";

    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// 必须在工作目录切换之后、Avalonia 启动之前调用: ConfigPath 是相对路径, 而读 IsEnabled
    /// 的那几处 (auth_token 校验、LLBot 启动参数) 都在 Avalonia 起来之后。
    /// 这里自己同步读一次而不走 ConfigManager —— DI 容器要等 App.Initialize 才建好。
    /// </summary>
    public static void Initialize()
    {
        IsEnabled = ReadConfigDev();
    }

    private static bool ReadConfigDev()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), AppJsonContext.Default.AppConfig);
            return config?.Dev ?? false;
        }
        catch
        {
            // 配置坏了不该拦住启动: ConfigManager 随后会走它自己的 fallback 并把错误记进日志
            return false;
        }
    }
}
