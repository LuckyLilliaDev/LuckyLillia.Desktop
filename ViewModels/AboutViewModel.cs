using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class AboutViewModel : ViewModelBase
{
    private readonly ILogger<AboutViewModel> _logger;
    private readonly IConfigManager _configManager;
    private readonly IUpdateChecker _updateChecker;
    private readonly IDownloadService _downloadService;
    private readonly IProcessManager _processManager;
    private readonly IUpdateStateService _updateStateService;

    private string? _pendingAppUpdateScript;

    // 对话框委托
    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }

    // 更新前停服务 / 更新后重启服务, 由控制面板实现
    public Func<Task>? StopServicesCallback { get; set; }
    public Func<Task>? RestartServicesCallback { get; set; }

    // GitHub 仓库地址
    private const string AppGitHubUrl = "https://github.com/LLOneBot/LuckyLilliaDesktop.Avalonia";
    private const string LLBotGitHubUrl = "https://github.com/LLOneBot/LuckyLilliaBot";
    private const string QQGroupUrl = "https://qm.qq.com/q/4XrMj9iReU";

    // 应用信息
    public static string AppName => "Lucky Lillia Desktop";
    public string AppVersion { get; }

    // LLBot 版本
    private string _llbotVersion = "检测中...";
    public string LLBotVersion
    {
        get => _llbotVersion;
        set => this.RaiseAndSetIfChanged(ref _llbotVersion, value);
    }

    // 更新状态 - App
    private bool _appIsLatest;
    public bool AppIsLatest
    {
        get => _appIsLatest;
        set => this.RaiseAndSetIfChanged(ref _appIsLatest, value);
    }

    private bool _appHasUpdate;
    public bool AppHasUpdate
    {
        get => _appHasUpdate;
        set => this.RaiseAndSetIfChanged(ref _appHasUpdate, value);
    }

    private string _appLatestVersion = string.Empty;
    public string AppLatestVersion
    {
        get => _appLatestVersion;
        set => this.RaiseAndSetIfChanged(ref _appLatestVersion, value);
    }

    private string _appReleaseUrl = string.Empty;
    public string AppReleaseUrl
    {
        get => _appReleaseUrl;
        set => this.RaiseAndSetIfChanged(ref _appReleaseUrl, value);
    }

    // 检查更新状态
    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        set => this.RaiseAndSetIfChanged(ref _isCheckingUpdate, value);
    }

    private string _checkUpdateButtonText = "检查更新";
    public string CheckUpdateButtonText
    {
        get => _checkUpdateButtonText;
        set => this.RaiseAndSetIfChanged(ref _checkUpdateButtonText, value);
    }

    private bool _hasAnyUpdate;
    public bool HasAnyUpdate
    {
        get => _hasAnyUpdate;
        set => this.RaiseAndSetIfChanged(ref _hasAnyUpdate, value);
    }

    // 下载更新状态
    private bool _isDownloadingUpdate;
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        set => this.RaiseAndSetIfChanged(ref _isDownloadingUpdate, value);
    }

    private string _downloadStatus = string.Empty;
    public string DownloadStatus
    {
        get => _downloadStatus;
        set => this.RaiseAndSetIfChanged(ref _downloadStatus, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    // 命令
    public ReactiveCommand<Unit, Unit> CheckOrUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAppReleaseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDocsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQQGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAppGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLLBotGitHubCommand { get; }

    public AboutViewModel(
        ILogger<AboutViewModel> logger,
        IConfigManager configManager,
        IUpdateChecker updateChecker,
        IDownloadService downloadService,
        IProcessManager processManager,
        IUpdateStateService updateStateService)
    {
        _logger = logger;
        _configManager = configManager;
        _updateChecker = updateChecker;
        _downloadService = downloadService;
        _processManager = processManager;
        _updateStateService = updateStateService;

        // 获取应用版本
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        AppVersion = version != null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";

        // 检查更新/立即更新命令
        CheckOrUpdateCommand = ReactiveCommand.CreateFromTask(CheckOrUpdateAsync, outputScheduler: AvaloniaUiScheduler.Instance);

        // 打开链接命令
        OpenAppReleaseCommand = ReactiveCommand.Create(() => OpenUrl(AppReleaseUrl), outputScheduler: AvaloniaUiScheduler.Instance);
        OpenDocsCommand = ReactiveCommand.Create(() => OpenUrl("https://luckylillia.com"), outputScheduler: AvaloniaUiScheduler.Instance);
        OpenQQGroupCommand = ReactiveCommand.Create(() => OpenUrl(QQGroupUrl), outputScheduler: AvaloniaUiScheduler.Instance);
        OpenAppGitHubCommand = ReactiveCommand.Create(() => OpenUrl(AppGitHubUrl), outputScheduler: AvaloniaUiScheduler.Instance);
        OpenLLBotGitHubCommand = ReactiveCommand.Create(() => OpenUrl(LLBotGitHubUrl), outputScheduler: AvaloniaUiScheduler.Instance);

        // 订阅共享更新状态
        _updateStateService.StateChanged
            .ObserveOnUiThread()
            .Subscribe(OnUpdateStateChanged);

        // 初始化版本检测
        _ = LoadVersionsAsync();
    }

    private void OnUpdateStateChanged(UpdateState state)
    {
        if (!state.IsChecked) return;

        AppHasUpdate = state.AppHasUpdate;
        AppLatestVersion = state.AppLatestVersion;
        AppReleaseUrl = state.AppReleaseUrl;
        AppIsLatest = !state.AppHasUpdate;

        HasAnyUpdate = state.HasAnyUpdate;
        CheckUpdateButtonText = HasAnyUpdate ? "立即更新" : "检查更新";
    }

    /// <summary>
    /// 加载本地版本信息
    /// </summary>
    public async Task LoadVersionsAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();

            // 检测 LLBot 版本
            var llbotPath = config.LLBotPath;
            if (!string.IsNullOrEmpty(llbotPath))
            {
                var llbotVersion = VersionDetector.DetectLLBotVersion(llbotPath, _logger);
                LLBotVersion = llbotVersion ?? "未知";
            }
            else
            {
                LLBotVersion = "未配置";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载版本信息失败");
            LLBotVersion = "检测失败";
        }
    }

    private async Task CheckOrUpdateAsync()
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return;

        if (HasAnyUpdate)
        {
            await DownloadAllUpdatesAsync();
        }
        else
        {
            await CheckUpdatesAsync();
        }
    }

    private async Task CheckUpdatesAsync()
    {
        IsCheckingUpdate = true;
        CheckUpdateButtonText = "检查中...";

        // 清除之前的更新状态
        AppIsLatest = false;
        AppHasUpdate = false;
        HasAnyUpdate = false;

        try
        {
            _logger.LogInformation("开始检查管理器更新...");
            await LoadVersionsAsync();

            // 仅检查桌面端管理器更新；LLBot 本体更新统一由 WebUI 负责
            var appUpdate = await _updateChecker.CheckAppUpdateAsync(AppVersion);
            var state = new UpdateState { IsChecked = true };
            if (appUpdate.HasUpdate)
            {
                AppHasUpdate = true;
                AppLatestVersion = appUpdate.LatestVersion;
                AppReleaseUrl = appUpdate.ReleaseUrl;
                state.AppHasUpdate = true;
                state.AppLatestVersion = appUpdate.LatestVersion;
                state.AppReleaseUrl = appUpdate.ReleaseUrl;
                _logger.LogInformation("发现应用新版本: {Version}", appUpdate.LatestVersion);
            }
            else
            {
                AppIsLatest = true;
            }

            _updateStateService.UpdateState(state);

            // 更新按钮状态
            HasAnyUpdate = AppHasUpdate;
            CheckUpdateButtonText = HasAnyUpdate ? "立即更新" : "检查更新";

            _logger.LogInformation("更新检查完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查更新失败");
            CheckUpdateButtonText = "检查更新";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private async Task DownloadAllUpdatesAsync()
    {
        if (IsDownloadingUpdate || !AppHasUpdate) return;

        var wasRunning = _processManager.IsRunning;

        // 如果服务正在运行，弹出确认对话框
        if (wasRunning && ConfirmDialog != null)
        {
            var confirmed = await ConfirmDialog("确认更新", "LLBot 正在运行中，更新管理器需要先停止它。\n\n确定要继续吗？");
            if (!confirmed)
            {
                _logger.LogInformation("用户取消更新");
                return;
            }
        }

        IsDownloadingUpdate = true;
        DownloadProgress = 0;

        try
        {
            // 如果有进程在运行，先停止
            if (wasRunning)
            {
                DownloadStatus = "正在停止 LLBot...";
                _logger.LogInformation("更新前停止 LLBot...");
                if (StopServicesCallback != null) await StopServicesCallback();
                else await _processManager.StopAsync();
                await Task.Delay(1000);
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadProgress = p.Percentage;
                DownloadStatus = p.Status;
            });

            DownloadStatus = "正在更新: 管理器";
            DownloadProgress = 0;

            var result = await _downloadService.DownloadAppUpdateAsync(progress);
            if (result.Success)
            {
                _pendingAppUpdateScript = result.UpdateScriptPath;
                AppHasUpdate = false;
                AppIsLatest = true;
                _updateStateService.ClearUpdate("管理器");
            }

            DownloadStatus = "更新完成！";
            DownloadProgress = 100;

            // 重新加载版本信息
            await LoadVersionsAsync();

            // 重置按钮状态
            HasAnyUpdate = false;
            CheckUpdateButtonText = "检查更新";

            // 如果有管理器更新，提示重启
            if (!string.IsNullOrEmpty(_pendingAppUpdateScript))
            {
                await Task.Delay(500);
                LaunchAppUpdate();
            }
            else if (wasRunning && RestartServicesCallback != null)
            {
                // 下载未生成更新脚本且更新前服务在运行，自动恢复启动
                _logger.LogInformation("恢复启动 LLBot...");
                DownloadStatus = "正在重新启动 LLBot...";
                await Task.Delay(500);
                await RestartServicesCallback();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载更新失败");
            DownloadStatus = $"更新失败: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private void LaunchAppUpdate()
    {
        if (string.IsNullOrEmpty(_pendingAppUpdateScript)) return;

        _logger.LogInformation("启动应用更新脚本: {Script}", _pendingAppUpdateScript);

        try
        {
            var workDir = Path.GetDirectoryName(_pendingAppUpdateScript) ?? "";

            if (PlatformHelper.IsWindows)
            {
                // Windows: 必须脱离 Job Object，否则主进程退出时脚本也会被杀掉
                if (!_processManager.StartProcessOutsideJob(_pendingAppUpdateScript, workDir))
                {
                    _logger.LogError("通过 Job Breakaway 启动更新脚本失败，回退到 ShellExecute");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _pendingAppUpdateScript,
                        UseShellExecute = true,
                        WorkingDirectory = workDir
                    });
                }
            }
            else
            {
                // macOS/Linux: 在终端窗口中执行脚本，这样用户可以看到执行过程
                if (PlatformHelper.IsMacOS)
                {
                    // macOS: 使用 open -a Terminal 在新终端窗口中执行
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = $"-a Terminal \"{_pendingAppUpdateScript}\"",
                        UseShellExecute = false,
                        WorkingDirectory = workDir
                    });
                }
                else
                {
                    // Linux: 使用 bash 执行
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"\"{_pendingAppUpdateScript}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workDir
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动更新脚本失败");
            return;
        }

        _pendingAppUpdateScript = null;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// 打开 URL
    /// </summary>
    private void OpenUrl(string url)
    {
        _logger.LogInformation("尝试打开链接: {Url}", url);
        if (string.IsNullOrEmpty(url))
        {
            _logger.LogWarning("链接为空，无法打开");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开失败: {Url}", url);
        }
    }

    /// <summary>
    /// 刷新页面
    /// </summary>
    public async Task RefreshAsync()
    {
        await LoadVersionsAsync();
    }
}
