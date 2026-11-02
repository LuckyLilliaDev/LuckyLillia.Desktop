using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Utils;

/// <summary>读取随 Bot 分发的版本元数据，不运行独立 PMHQ。</summary>
public static class VersionDetector
{
    /// <summary>
    /// 检测 LLBot 版本: 读取脚本同目录的 package.json。
    /// </summary>
    public static string? DetectLLBotVersion(string? llbotPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(llbotPath)) return null;
        var llbotDir = Path.GetDirectoryName(llbotPath);
        return ReadVersionFromPackageJson(llbotDir, logger, "LLBot");
    }

    private static string? ReadVersionFromPackageJson(string? dir, ILogger? logger, string component)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) return null;

            var packageJsonPath = Path.Combine(dir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var json = File.ReadAllText(packageJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var versionElement))
                {
                    return versionElement.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "从 package.json 检测 {Component} 版本失败", component);
        }
        return null;
    }

}
