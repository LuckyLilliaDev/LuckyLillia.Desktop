namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 应用程序常量定义
/// </summary>
public static class Constants
{
    /// <summary>
    /// 应用显示名称（窗口标题 / 托盘）
    /// </summary>
    public const string AppDisplayName = "Lucky Lillia";

    /// <summary>
    /// NPM 包名映射
    /// </summary>
    public static class NpmPackages
    {
        public const string LLBot = "@luckylillia-dev/luckylillia";
        public static string Node => PlatformHelper.GetPlatformPackageName("@luckylillia-dev/node");
        public static string App => PlatformHelper.GetPlatformPackageName("lucky-lillia-desktop");
    }

    /// <summary>
    /// GitHub 仓库映射
    /// </summary>
    public static class GitHubRepos
    {
        public const string LLBot = "LLOneBot/LuckyLilliaBot";
        public const string App = "LLOneBot/LuckyLilliaDesktop.Avalonia";
    }

    /// <summary>
    /// 默认路径
    /// </summary>
    public static class DefaultPaths
    {
        public const string LLBotDir = "bin/llbot";
        public const string LLBotScript = "bin/llbot/llbot.js";
        public static string NodeExe => PlatformHelper.GetExecutablePath("bin/llbot", "node");
    }

    /// <summary>
    /// NPM 镜像源（下载优先）
    /// </summary>
    public static readonly string[] NpmDownloadMirrors =
    {
        "https://registry.npmmirror.com",
        "https://mirrors.huaweicloud.com/repository/npm",
        "https://mirrors.cloud.tencent.com/npm",
        "https://registry.npmjs.org"
    };

    /// <summary>
    /// NPM 官方源（版本检查优先）
    /// </summary>
    public static readonly string[] NpmVersionCheckMirrors =
    {
        "https://registry.npmjs.org",
        "https://registry.npmmirror.com",
        "https://mirrors.huaweicloud.com/repository/npm",
        "https://mirrors.cloud.tencent.com/npm"
    };

    /// <summary>
    /// 超时设置
    /// </summary>
    public static class Timeouts
    {
        public const int UpdateCheck = 10;
        public const int Download = 300;
    }
}
