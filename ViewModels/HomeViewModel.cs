using Avalonia.Threading;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using LuckyLilliaDesktop.Utils.WebView2;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

/// <summary>运行环境 (Node.js / LLBot) 的准备阶段.</summary>
public enum RuntimePhase
{
    Idle,
    Preparing,
    Downloading,
    Ready,
    Failed
}

/// <summary>LLBot 统一管理进程的运行阶段.</summary>
public enum ServicePhase
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

/// <summary>
/// 控制面板: 准备运行时 -> 拉起唯一的 LLBot 管理进程 (llbot.js -> llbot-manager.js) -> 等统一网关 WebUI 就绪 -> 嵌进 WebView2.
/// 多实例增删改启停、登录方式、协议与 WebUI 密码全在 LLBot 统一 WebUI 里处理. 见 doc/llbot-webui.md.
/// </summary>
public class HomeViewModel : ViewModelBase
{
    private static readonly TimeSpan WebUiReadyTimeout = TimeSpan.FromSeconds(90);
    // 启动门槛: LLBot 主版本必须 >= 8
    private const int MinMajorVersion = 8;

    private readonly IProcessManager _processManager;
    private readonly IConfigManager _configManager;
    private readonly ISystemTimeChecker _systemTimeChecker;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateChecker _updateChecker;
    private readonly IUpdateStateService _updateStateService;
    private readonly ILogCollector _logCollector;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _serviceGate = new(1, 1);
    private CancellationTokenSource? _downloadCts;
    private AppConfig _config = AppConfig.Default;
    private bool _startupCommandDone;
    private bool _resumeAfterUpdate;
    private bool _legacyMigrated;

    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    public Func<string, string, Task>? ShowAlertDialog { get; set; }
    public Func<string, Func<Task>, Task>? ShowLoadingDialog { get; set; }
    public Action? NavigateToAbout { get; set; }
    public Func<Task>? OnDownloadCompleted { get; set; }

    /// <summary>终端日志: LLBot 管理进程及其子实例的输出加 Desktop 自己的日志, 工具条点 "终端日志" 后顶替 WebView 显示.</summary>
    public LogViewModel TerminalLog { get; }

    private bool _showTerminal;
    public bool ShowTerminal
    {
        get => _showTerminal;
        set
        {
            if (_showTerminal == value) return;
            this.RaiseAndSetIfChanged(ref _showTerminal, value);
            RaiseDerived();
            if (value) TerminalLog.ScrollToBottom();
        }
    }

    // ---- 运行环境 ----

    private RuntimePhase _runtimePhase = RuntimePhase.Idle;
    public RuntimePhase RuntimePhase
    {
        get => _runtimePhase;
        private set
        {
            if (_runtimePhase == value) return;
            this.RaiseAndSetIfChanged(ref _runtimePhase, value);
            RaiseDerived();
        }
    }

    private string _runtimeMessage = string.Empty;
    public string RuntimeMessage
    {
        get => _runtimeMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _runtimeMessage, value);
            RaiseDerived();
        }
    }

    // ---- LLBot 进程状态 ----

    private ServicePhase _servicePhase = ServicePhase.Stopped;
    public ServicePhase ServicePhase
    {
        get => _servicePhase;
        private set
        {
            if (_servicePhase == value) return;
            this.RaiseAndSetIfChanged(ref _servicePhase, value);
            // 页面关闭只对这一次运行有效, 停了再启动时 WebUI 照常显示
            if (value != ServicePhase.Running) _isWebUiClosed = false;
            RaiseDerived();
        }
    }

    private bool _isWebUiClosed;
    public bool IsWebUiClosed
    {
        get => _isWebUiClosed;
        private set
        {
            if (_isWebUiClosed == value) return;
            this.RaiseAndSetIfChanged(ref _isWebUiClosed, value);
            RaiseDerived();
        }
    }

    private int? _port;
    public int? Port
    {
        get => _port;
        private set
        {
            if (_port == value) return;
            this.RaiseAndSetIfChanged(ref _port, value);
            RaiseDerived();
        }
    }

    private string? _url;
    private int? _exitCode;
    private string _serviceMessage = string.Empty;

    // 内嵌视图是否可用: 非 Windows / 没装运行时 / 初始化失败 都为 false, 页面改成"在浏览器打开"
    private bool _webViewSupported = WebView2Runtime.IsAvailable;
    public bool WebViewSupported
    {
        get => _webViewSupported;
        private set
        {
            this.RaiseAndSetIfChanged(ref _webViewSupported, value);
            RaiseDerived();
        }
    }

    private string _webViewFailure = string.Empty;

    private string _pageError = string.Empty;
    public string PageError
    {
        get => _pageError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pageError, value);
            this.RaisePropertyChanged(nameof(HasPageError));
        }
    }

    public bool HasPageError => !string.IsNullOrEmpty(PageError);

    // ---- 派生状态 ----

    public bool IsRuntimeReady => RuntimePhase == RuntimePhase.Ready;
    public bool IsServiceRunning => ServicePhase == ServicePhase.Running;
    public bool HasPort => Port != null;
    public string PortText => Port is int p ? $":{p}" : string.Empty;
    public ProcessStatus BotStatus => ServicePhase switch
    {
        ServicePhase.Running => ProcessStatus.Running,
        ServicePhase.Starting => ProcessStatus.Starting,
        ServicePhase.Stopping => ProcessStatus.Stopping,
        ServicePhase.Failed => ProcessStatus.Error,
        _ => RuntimePhase switch
        {
            RuntimePhase.Preparing or RuntimePhase.Downloading => ProcessStatus.Starting,
            RuntimePhase.Failed => ProcessStatus.Error,
            _ => ProcessStatus.Stopped
        }
    };
    public bool ShowTerminalView => ShowTerminal;
    public bool CanShowTerminal => true;
    public bool ShowWebView => IsRuntimeReady && IsServiceRunning && WebViewSupported && !IsWebUiClosed && !ShowTerminalView;
    public bool CanToggleWebUi => IsRuntimeReady && IsServiceRunning && WebViewSupported;
    public string ToggleWebUiText => IsWebUiClosed ? "打开页面" : "关闭页面";
    public bool ShowReopenWebUi => CanToggleWebUi && IsWebUiClosed;
    public bool ShowOverlay => !ShowWebView && !ShowTerminalView;
    public bool ShowSpinner => RuntimePhase == RuntimePhase.Preparing || ServicePhase is ServicePhase.Starting or ServicePhase.Stopping;
    public bool ShowProgress => RuntimePhase == RuntimePhase.Downloading;
    public bool ShowCancelDownload => RuntimePhase == RuntimePhase.Downloading;
    public bool ShowStartButton => RuntimePhase == RuntimePhase.Failed
        || (IsRuntimeReady && ServicePhase is ServicePhase.Stopped or ServicePhase.Failed);
    public string StartButtonText => RuntimePhase == RuntimePhase.Failed || ServicePhase == ServicePhase.Failed ? "重试" : "启动";
    public bool ShowOpenInBrowser => IsServiceRunning && !WebViewSupported;
    public bool ShowInstallWebView2 => IsServiceRunning && !WebViewSupported && OperatingSystem.IsWindows() && string.IsNullOrEmpty(_webViewFailure);
    public bool ShowLogsButton => RuntimePhase == RuntimePhase.Failed || ServicePhase is ServicePhase.Stopped or ServicePhase.Failed;
    public bool ShowErrorIcon => RuntimePhase == RuntimePhase.Failed
        || ServicePhase == ServicePhase.Failed
        || (ServicePhase == ServicePhase.Stopped && _exitCode is not (null or 0));
    public bool CanStartStop => RuntimePhase == RuntimePhase.Failed
        || (IsRuntimeReady && ServicePhase is ServicePhase.Running or ServicePhase.Stopped or ServicePhase.Failed);
    public string StartStopText => IsServiceRunning ? "停止" : "启动";
    public bool CanRestart => IsServiceRunning;
    public bool CanRefresh => ShowWebView;
    public bool HasWebUiUrl => IsServiceRunning && !string.IsNullOrEmpty(_url);
    public string? WebUiUrl => HasWebUiUrl ? _url : null;

    public string OverlayTitle
    {
        get
        {
            switch (RuntimePhase)
            {
                case RuntimePhase.Idle: return "控制面板";
                case RuntimePhase.Preparing: return "正在准备运行环境";
                case RuntimePhase.Downloading: return $"正在下载 {DownloadingItem}";
                case RuntimePhase.Failed: return "运行环境准备失败";
            }
            return ServicePhase switch
            {
                ServicePhase.Starting => "正在启动 LLBot",
                ServicePhase.Stopping => "正在停止 LLBot",
                ServicePhase.Failed => "LLBot 启动失败",
                ServicePhase.Running when IsWebUiClosed && WebViewSupported => "页面已关闭",
                ServicePhase.Running when !string.IsNullOrEmpty(_webViewFailure) => "内嵌 WebView 初始化失败",
                ServicePhase.Running when OperatingSystem.IsWindows() => "未安装 WebView2 运行时",
                ServicePhase.Running => "当前系统不支持内嵌 WebView",
                _ => "LLBot 未运行"
            };
        }
    }

    public string OverlayMessage
    {
        get
        {
            switch (RuntimePhase)
            {
                case RuntimePhase.Preparing: return "检查 Node.js 与 LLBot，缺失的组件会自动下载";
                case RuntimePhase.Downloading: return string.Empty;
                case RuntimePhase.Failed: return RuntimeMessage;
            }
            return ServicePhase switch
            {
                ServicePhase.Starting => Port is int port ? $"等待 WebUI 就绪 (端口 {port})" : string.Empty,
                ServicePhase.Running when IsWebUiClosed && WebViewSupported => "LLBot 仍在后台运行，只是关掉了页面显示。",
                ServicePhase.Running => string.IsNullOrEmpty(_webViewFailure)
                    ? $"可以先在系统浏览器中打开 WebUI: {_url}"
                    : $"{_webViewFailure}\n可以先在系统浏览器中打开 WebUI: {_url}",
                _ => _serviceMessage
            };
        }
    }

    public bool HasOverlayMessage => !string.IsNullOrEmpty(OverlayMessage);

    private static readonly string[] DerivedProperties =
    [
        nameof(IsRuntimeReady), nameof(IsServiceRunning), nameof(HasPort), nameof(PortText), nameof(BotStatus),
        nameof(ShowWebView), nameof(ShowOverlay), nameof(ShowTerminalView), nameof(CanShowTerminal),
        nameof(ShowSpinner), nameof(ShowProgress), nameof(ShowCancelDownload), nameof(ShowStartButton), nameof(StartButtonText),
        nameof(ShowOpenInBrowser), nameof(ShowInstallWebView2), nameof(ShowLogsButton), nameof(ShowErrorIcon),
        nameof(CanStartStop), nameof(StartStopText), nameof(CanRestart), nameof(CanRefresh),
        nameof(HasWebUiUrl), nameof(WebUiUrl), nameof(OverlayTitle), nameof(OverlayMessage), nameof(HasOverlayMessage),
        nameof(IsWebUiClosed), nameof(CanToggleWebUi), nameof(ToggleWebUiText), nameof(ShowReopenWebUi)
    ];

    private void RaiseDerived()
    {
        foreach (var name in DerivedProperties) this.RaisePropertyChanged(name);
    }

    // ---- 下载进度 ----

    private string _downloadingItem = string.Empty;
    public string DownloadingItem
    {
        get => _downloadingItem;
        private set
        {
            this.RaiseAndSetIfChanged(ref _downloadingItem, value);
            RaiseDerived();
        }
    }

    private string _downloadStatus = string.Empty;
    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => this.RaiseAndSetIfChanged(ref _downloadStatus, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    // ---- 更新提示 ----

    private bool _hasUpdate;
    public bool HasUpdate
    {
        get => _hasUpdate;
        private set => this.RaiseAndSetIfChanged(ref _hasUpdate, value);
    }

    private string _updateBannerText = "发现新版本";
    public string UpdateBannerText
    {
        get => _updateBannerText;
        private set => this.RaiseAndSetIfChanged(ref _updateBannerText, value);
    }

    // ---- 命令 ----

    public ReactiveCommand<Unit, Unit> StartStopCommand { get; }
    public ReactiveCommand<Unit, Unit> RestartCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenInBrowserCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallWebView2Command { get; }
    public ReactiveCommand<Unit, Unit> CancelDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleWebUiCommand { get; }
    public ReactiveCommand<Unit, Unit> ReopenWebUiCommand { get; }

    public HomeViewModel(
        IProcessManager processManager,
        IConfigManager configManager,
        ISystemTimeChecker systemTimeChecker,
        IDownloadService downloadService,
        IUpdateChecker updateChecker,
        IUpdateStateService updateStateService,
        LogViewModel terminalLog,
        ILogCollector logCollector,
        ILogger<HomeViewModel> logger)
    {
        TerminalLog = terminalLog;
        _logCollector = logCollector;
        _processManager = processManager;
        _configManager = configManager;
        _systemTimeChecker = systemTimeChecker;
        _downloadService = downloadService;
        _updateChecker = updateChecker;
        _updateStateService = updateStateService;
        _logger = logger;

        _processManager.StatusChanged += OnProcessStatusChanged;
        _updateStateService.StateChanged
            .ObserveOnUiThread()
            .Subscribe(OnUpdateStateChanged);

        // 按钮启用状态由属性绑定控制, 不用 canExecute observable (ReactiveCommand 的 CanExecuteChanged 会跨线程触发)
        StartStopCommand = ReactiveCommand.Create(() =>
        {
            if (!CanStartStop) return;
            if (RuntimePhase == RuntimePhase.Failed) _ = RetryRuntimeAsync();
            else _ = IsServiceRunning ? StopAsync() : StartAsync();
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        RestartCommand = ReactiveCommand.Create(() =>
        {
            if (CanRestart) _ = RestartAsync();
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        OpenInBrowserCommand = ReactiveCommand.Create(() =>
        {
            if (HasWebUiUrl) OpenExternal(_url!);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        InstallWebView2Command = ReactiveCommand.Create(() => OpenExternal(WebView2Runtime.InstallerUrl), outputScheduler: AvaloniaUiScheduler.Instance);
        CancelDownloadCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("用户取消下载");
            _downloadCts?.Cancel();
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        UpdateCommand = ReactiveCommand.Create(() => NavigateToAbout?.Invoke(), outputScheduler: AvaloniaUiScheduler.Instance);
        ViewLogsCommand = ReactiveCommand.Create(() => { ShowTerminal = true; }, outputScheduler: AvaloniaUiScheduler.Instance);
        ShowHomeCommand = ReactiveCommand.Create(() => { ShowTerminal = false; }, outputScheduler: AvaloniaUiScheduler.Instance);
        OpenLogsCommand = ReactiveCommand.Create(() => OpenExternal(Path.GetFullPath("logs")), outputScheduler: AvaloniaUiScheduler.Instance);
        ToggleWebUiCommand = ReactiveCommand.Create(() =>
        {
            if (CanToggleWebUi) SetWebUiClosed(!IsWebUiClosed);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        ReopenWebUiCommand = ReactiveCommand.Create(() =>
        {
            if (CanToggleWebUi) SetWebUiClosed(false);
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        _ = CheckForUpdatesAsync();
        _ = InitializeAsync();
    }

    // ---- 启动流程 ----

    private async Task InitializeAsync()
    {
        try
        {
            _config = await _configManager.LoadConfigAsync();
            // 让主窗口先画出来
            await Task.Delay(300);
            if (!await EnsureRuntimeAsync()) return;
            await StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "控制面板初始化失败");
        }
    }

    private async Task RetryRuntimeAsync()
    {
        RuntimePhase = RuntimePhase.Idle;
        if (!await EnsureRuntimeAsync()) return;
        await StartAsync();
    }

    /// <summary>一次性把开发版 Desktop 存在 app_settings.json 里的多实例自启配置迁到 LLBot 的 data/manager.json.</summary>
    private async Task MigrateLegacyDesktopMultiInstanceAsync(AppConfig config)
    {
        if (_legacyMigrated) return;
        _legacyMigrated = true;

        var legacyAutoStart = _configManager.GetStringListSetting("auto_start_instances");
        if (legacyAutoStart != null)
        {
            if (LLBotWebUi.MigrateAutoStartToManagerJson(config.LLBotPath, legacyAutoStart))
            {
                _logger.LogInformation("已将开发版 auto_start_instances ({Instances}) 迁移到 data/manager.json",
                    string.Join(", ", legacyAutoStart));
            }
            await _configManager.RemoveSettingAsync("auto_start_instances");
        }

        var legacyDefaultPort = _configManager.GetIntDictionarySetting("instance_ports", LLBotWebUi.DefaultInstance);
        if (legacyDefaultPort is > 0 and <= 65535 && config.WebUiFallbackPort == null)
        {
            config.WebUiFallbackPort = legacyDefaultPort;
            await _configManager.SetSettingAsync("webui_fallback_port", legacyDefaultPort);
        }
        await _configManager.RemoveSettingAsync("instance_ports");
    }

    public async Task StartAsync()
    {
        if (ServicePhase is not (ServicePhase.Stopped or ServicePhase.Failed)) return;
        if (!await EnsureRuntimeAsync()) return;

        var config = _config;
        var gateHeld = false;
        try
        {
            await _serviceGate.WaitAsync();
            gateHeld = true;
            if (ServicePhase is not (ServicePhase.Stopped or ServicePhase.Failed)) return;

            ServicePhase = ServicePhase.Starting;
            _serviceMessage = string.Empty;
            _exitCode = null;
            _url = null;
            Port = null;
            PageError = string.Empty;

            var endpoint = LLBotWebUi.ReadGatewayEndpoint(config.LLBotPath);

            // 界面以为没在跑, 进程管理器里却还有 LLBot 进程 (状态不同步): 不起第二个, 直接接管它
            if (_processManager.Status == ProcessStatus.Running && _processManager.Pid is int livePid)
            {
                _logger.LogWarning("LLBot 进程 {Pid} 仍在运行, 直接接管", livePid);
                _serviceGate.Release();
                gateHeld = false;
                var adopted = await WaitForWebUiAsync(endpoint.ConnectHost, config.WebUiFallbackPort ?? endpoint.Port);
                if (ServicePhase != ServicePhase.Starting) return;
                if (adopted == null)
                {
                    await StopQuietlyAsync();
                    Fail($"LLBot 进程仍在运行，但等了 {WebUiReadyTimeout.TotalSeconds:0} 秒也没找到它的 WebUI，已停止该进程，请重试。检测结果: {_lastWebUiProbe}");
                    return;
                }
                _url = adopted;
                ServicePhase = ServicePhase.Running;
                _logger.LogInformation("LLBot WebUI 已就绪: {Url}", adopted);
                return;
            }

            // 端口: 网关配置里的是首选, 被占先回退到上次备用端口 (webui_fallback_port) 保持 origin/cookie 不变, 再不行顺延 / 系统分配
            int? free = null;
            if (TcpPorts.IsFree(endpoint.Port)) free = endpoint.Port;
            else if (config.WebUiFallbackPort is int remembered && TcpPorts.IsFree(remembered)) free = remembered;
            free ??= TcpPorts.FindFree(endpoint.Port);
            if (free == null)
            {
                Fail($"从端口 {endpoint.Port} 起找不到空闲端口");
                return;
            }
            var port = free.Value;
            if (port != endpoint.Port)
                _logger.LogWarning("配置端口 {Configured} 被占用, 改用 {Port}", endpoint.Port, port);
            Port = port;

            var started = await _processManager.StartAsync(
                new LLBotLaunchOptions(config.NodePath, config.LLBotPath, port, config.HttpProxy, config.ServerRegion == "china"));
            if (!started.Started)
            {
                _exitCode = _processManager.ExitCode;
                Fail(started.Error ?? "LLBot 启动失败，原因不明，请查看终端日志");
                return;
            }

            _serviceGate.Release();
            gateHeld = false;

            var url = await WaitForWebUiAsync(endpoint.ConnectHost, port);
            // 等待期间被停掉 / 进程自己退出: 状态已由别处填好
            if (ServicePhase != ServicePhase.Starting) return;
            if (url == null)
            {
                await StopQuietlyAsync();
                Fail($"等待 WebUI 就绪超时 ({WebUiReadyTimeout.TotalSeconds:0} 秒)。检测结果: {_lastWebUiProbe}");
                return;
            }

            _url = url;
            ServicePhase = ServicePhase.Running;
            _logger.LogInformation("LLBot WebUI 已就绪: {Url}", url);
            if (Port is int actual && actual != endpoint.Port) await RememberFallbackPortAsync(actual);

            if (!_startupCommandDone)
            {
                _startupCommandDone = true;
                await ExecuteStartupCommandAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 LLBot 时出错");
            if (ServicePhase == ServicePhase.Starting)
            {
                await StopQuietlyAsync();
                Fail($"启动 LLBot 时出错: {ex.Message}");
            }
        }
        finally
        {
            if (gateHeld) _serviceGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (ServicePhase is not (ServicePhase.Running or ServicePhase.Starting)) return;
        ServicePhase = ServicePhase.Stopping;
        _logger.LogInformation("停止 LLBot...");
        try
        {
            if (ShowLoadingDialog != null)
                await ShowLoadingDialog("正在停止 LLBot 及所有实例...", () => _processManager.StopAsync());
            else
                await _processManager.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止 LLBot 时出错");
        }
        _exitCode = _processManager.ExitCode ?? 0;
        _url = null;
        Port = null;
        _serviceMessage = "已手动停止";
        ServicePhase = ServicePhase.Stopped;
    }

    private async Task RestartAsync()
    {
        if (ConfirmDialog != null && !await ConfirmDialog("重启 LLBot", "确定要重启 LLBot 吗？所有正在运行的实例都会先停止再重新拉起。"))
            return;
        await StopAsync();
        await StartAsync();
    }

    /// <summary>关于页更新 LLBot 前调用: 停掉管理进程, 记住更新前是否在跑.</summary>
    public async Task StopForUpdateAsync()
    {
        _resumeAfterUpdate = ServicePhase is ServicePhase.Running or ServicePhase.Starting;
        if (_resumeAfterUpdate) await StopAsync();
    }

    /// <summary>关于页更新 LLBot 后调用: 重新过一遍运行环境检查 (版本变了), 更新前在跑就拉起来.</summary>
    public async Task ResumeAfterUpdateAsync()
    {
        RuntimePhase = RuntimePhase.Idle;
        if (!await EnsureRuntimeAsync()) return;
        if (_resumeAfterUpdate)
        {
            _resumeAfterUpdate = false;
            await StartAsync();
        }
    }

    // WebUI 前端自己存登录 cookie 时也是 30 天 (FE/utils/api.ts TOKEN_EXPIRY_DAYS)
    private static readonly TimeSpan WebUiLoginCookieLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// 内嵌 WebView 打开 <paramref name="url"/> 前要预置的统一 WebUI 登录 cookie: 从 data/webui_token.txt 算出来,
    /// 桌面端里打开 WebUI 不用输密码. 还没设密码或地址不匹配时返回 null.
    /// </summary>
    public Controls.WebViewCookie? WebUiLoginCookie(string url)
    {
        if (!IsServiceRunning || _url != url || Port is not int port) return null;
        var cookie = LLBotWebUi.LoginCookie(_config.LLBotPath, port);
        return cookie is { } c ? new Controls.WebViewCookie(url, c.Name, c.Value, WebUiLoginCookieLifetime) : null;
    }

    private async Task RememberFallbackPortAsync(int port)
    {
        if (_config.WebUiFallbackPort == port) return;
        _config.WebUiFallbackPort = port;
        await _configManager.SetSettingAsync("webui_fallback_port", port);
    }

    private void Fail(string message)
    {
        _logger.LogWarning("LLBot 启动中止: {Message}", message);
        _serviceMessage = message;
        _url = null;
        Port = null;
        ServicePhase = ServicePhase.Failed;
    }

    private async Task StopQuietlyAsync()
    {
        try { await _processManager.StopAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "清理 LLBot 进程失败"); }
    }

    private void OnProcessStatusChanged(object? sender, ProcessStatusChangedEventArgs e)
    {
        if (e.Status is not (ProcessStatus.Stopped or ProcessStatus.Error)) return;
        // 主动停止由发起方收尾, 避免重启时旧进程的 Stopped 事件晚到把新进程标成已退出
        if (e.Requested) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_processManager.Status is ProcessStatus.Running or ProcessStatus.Starting) return;
            if (ServicePhase is ServicePhase.Running or ServicePhase.Starting) MarkExited();
        });
    }

    /// <summary>LLBot 最近的几行输出 (去掉 ANSI 颜色), 失败提示里用.</summary>
    private string RecentOutput(int lines)
    {
        var recent = _logCollector.GetRecentLogs(300)
            .Where(e => e.ProcessName == ProcessManager.LogSourceName)
            .Select(e => AnsiEscape.Replace(e.Message, "").TrimEnd())
            .Where(m => m.Length > 0)
            .ToList();
        return string.Join("\n", recent.Skip(Math.Max(0, recent.Count - lines)));
    }

    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*[A-Za-z]");

    private void MarkExited()
    {
        var code = _processManager.ExitCode;
        _exitCode = code;
        var reason = code is null or 0 ? "LLBot 进程已退出" : $"LLBot 进程异常退出 (退出码 {code})";
        var tail = RecentOutput(5);
        _serviceMessage = tail.Length == 0 ? reason : $"{reason}，最后的输出:\n{tail}";
        _logger.LogWarning("LLBot: {Message}", _serviceMessage);
        _url = null;
        Port = null;
        ServicePhase = ServicePhase.Stopped;
    }

    // ---- WebUI 就绪 ----

    private string _lastWebUiProbe = string.Empty;

    private async Task<string?> WaitForWebUiAsync(string host, int port)
    {
        var deadline = DateTime.UtcNow + WebUiReadyTimeout;
        var warnedListenerFailure = false;
        var lastProbe = string.Empty;
        _logger.LogInformation("等待 LLBot WebUI: PID={Pid}, 地址={Url}",
            _processManager.Pid, LLBotWebUi.Url(host, port));
        while (DateTime.UtcNow < deadline)
        {
            if (ServicePhase != ServicePhase.Starting) return null;
            if (_processManager.Status != ProcessStatus.Running)
            {
                MarkExited();
                return null;
            }

            var pid = _processManager.Pid;
            var listeners = TcpListenerTable.GetListeners();
            // llbot.js -> llbot-manager.js -> 各实例 llbot-core.js; 网关端口由 llbot-manager.js 监听,
            // 各实例的内部端口没有 X-Webui-Auth 头, 不会被 IsWebUiAsync 误认
            var pids = pid is int rootPid ? ProcessTree.WithDescendants(rootPid) : null;
            if (listeners != null && pids != null)
            {
                var owned = listeners.Where(l => pids.Contains(l.Pid)).Select(l => l.Port).Distinct().ToList();
                var portOwners = listeners.Where(l => l.Port == port).Select(l => l.Pid).Distinct();
                lastProbe = $"PID={pid} (进程树 [{string.Join(", ", pids)}]), 进程树监听端口=[{string.Join(", ", owned)}], 目标端口 {port} 的 PID=[{string.Join(", ", portOwners)}]";
            }
            else
            {
                lastProbe = $"PID={pid}, 端口归属不可用，HTTP 探测 {LLBotWebUi.Url(host, port)} 未就绪";
                if (OperatingSystem.IsWindows() && !warnedListenerFailure)
                {
                    warnedListenerFailure = true;
                    _logger.LogWarning("无法读取 TCP 端口归属，改用本机 WebUI HTTP 特征检测 (PID={Pid}, 端口={Port})", pid, port);
                }
            }
            var readyPort = await LLBotWebUi.FindReadyPortAsync(port, pids, listeners,
                candidate => LLBotWebUi.IsWebUiAsync(host, candidate));
            if (readyPort is int actualPort)
            {
                if (actualPort != port)
                    _logger.LogWarning("LLBot 没有采用分配的端口 {Port}, 实际监听 {Actual}", port, actualPort);
                Port = actualPort;
                return LLBotWebUi.Url(host, actualPort);
            }

            await Task.Delay(500);
        }
        _logger.LogWarning("LLBot WebUI 就绪检测超时: {Probe}", lastProbe);
        _lastWebUiProbe = lastProbe;
        return null;
    }

    // ---- 运行时准备 ----

    private string? _checkedNodePath;
    private string? _checkedLLBotPath;

    private async Task<bool> RefreshReadyConfigAsync()
    {
        var config = await _configManager.LoadConfigAsync();
        if (config.NodePath != _checkedNodePath || config.LLBotPath != _checkedLLBotPath)
        {
            _logger.LogInformation("Node.js / LLBot 路径已修改, 重新检查运行环境");
            return false;
        }
        _config = config;
        return true;
    }

    private async Task<bool> EnsureRuntimeAsync()
    {
        if (RuntimePhase == RuntimePhase.Ready && await RefreshReadyConfigAsync()) return true;
        await _runtimeGate.WaitAsync();
        try
        {
            if (RuntimePhase == RuntimePhase.Ready && await RefreshReadyConfigAsync()) return true;
            RuntimePhase = RuntimePhase.Preparing;
            RuntimeMessage = string.Empty;
            var config = await _configManager.LoadConfigAsync();
            _config = config;

            if (!await CheckSystemTimeAsync(config.HttpProxy)) return RuntimeFailed(RuntimeMessage);

            var configChanged = false;
            if (!await ResolveNodePathAsync(config, () => configChanged = true)) return false;

            if (string.IsNullOrEmpty(config.LLBotPath) || !File.Exists(config.LLBotPath))
            {
                if (!await DownloadAsync("LLBot", _downloadService.DownloadLLBotAsync)) return false;
                config.LLBotPath = Constants.DefaultPaths.LLBotScript;
                configChanged = true;
            }

            RuntimePhase = RuntimePhase.Preparing;
            if (configChanged)
            {
                await _configManager.SaveConfigAsync(config);
                if (OnDownloadCompleted != null) await OnDownloadCompleted();
            }

            if (!File.Exists(config.NodePath)) return RuntimeFailed($"Node.js 文件不存在: {config.NodePath}");
            if (!File.Exists(config.LLBotPath)) return RuntimeFailed($"LLBot 脚本不存在: {config.LLBotPath}");

            var versionError = CheckVersionRequirement(config);
            if (versionError != null) return RuntimeFailed(versionError);

            await MigrateLegacyDesktopMultiInstanceAsync(config);

            _checkedNodePath = config.NodePath;
            _checkedLLBotPath = config.LLBotPath;
            RuntimePhase = RuntimePhase.Ready;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "准备运行环境时出错");
            return RuntimeFailed($"准备运行环境时出错: {ex.Message}");
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private bool RuntimeFailed(string message)
    {
        _logger.LogWarning("运行环境准备失败: {Message}", message);
        RuntimeMessage = message;
        RuntimePhase = RuntimePhase.Failed;
        return false;
    }

    // Node.js: 配置 -> PATH -> 本地目录, 都没有 (或版本 < 24) 就下载. 成功时把路径写回 config.NodePath.
    private async Task<bool> ResolveNodePathAsync(AppConfig config, Action onConfigChanged)
    {
        if (await TryUseNodeAsync(config.NodePath, "配置中的 Node.js")) return true;

        var systemNode = NodeHelper.FindNodeInPath();
        if (await TryUseNodeAsync(systemNode, "系统 PATH 中的 Node.js"))
        {
            config.NodePath = systemNode!;
            onConfigChanged();
            return true;
        }

        var localNode = Constants.DefaultPaths.NodeExe;
        if (await TryUseNodeAsync(localNode, "本地目录的 Node.js"))
        {
            config.NodePath = localNode;
            onConfigChanged();
            return true;
        }

        if (!await DownloadAsync("Node.js", _downloadService.DownloadNodeAsync)) return false;
        config.NodePath = Constants.DefaultPaths.NodeExe;
        onConfigChanged();
        return true;
    }

    private async Task<bool> TryUseNodeAsync(string? path, string description)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        if (!await NodeHelper.CheckNodeVersionValidAsync(path, 24, _logger))
        {
            _logger.LogWarning("{Description} 版本低于 24, 跳过: {Path}", description, path);
            return false;
        }
        _logger.LogInformation("使用{Description}: {Path}", description, path);
        return true;
    }

    private async Task<bool> DownloadAsync(string item, Func<IProgress<DownloadProgress>?, CancellationToken, Task<bool>> download)
    {
        _downloadCts?.Dispose();
        var cts = _downloadCts = new CancellationTokenSource();
        DownloadingItem = item;
        DownloadProgress = 0;
        DownloadStatus = "正在获取下载地址...";
        RuntimePhase = RuntimePhase.Downloading;
        _logger.LogInformation("下载 {Item}...", item);
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadProgress = p.Percentage;
                DownloadStatus = p.Status;
            });
            var ok = await download(progress, cts.Token);
            if (!ok) RuntimeFailed(cts.IsCancellationRequested ? $"{item} 下载已取消" : $"{item} 下载失败，请检查网络后重试");
            return ok;
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
            cts.Dispose();
        }
    }

    private static int? ParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var seg = version.Trim().TrimStart('v', 'V').Split('.', '-', '+')[0];
        return int.TryParse(seg, out var major) ? major : null;
    }

    private string? CheckVersionRequirement(AppConfig config)
    {
        var llbotVer = VersionDetector.DetectLLBotVersion(config.LLBotPath, _logger);
        var llbotMajor = ParseMajorVersion(llbotVer);
        if (llbotMajor == null)
            return $"无法确认 LLBot 版本, 请更新到 {MinMajorVersion}.0 或以上后再启动";
        if (llbotMajor < MinMajorVersion)
            return $"LLBot 版本过低 (当前 {llbotVer}), 需要 {MinMajorVersion}.0 或以上, 请更新后再启动";
        return null;
    }

    private async Task<bool> CheckSystemTimeAsync(string? httpProxy)
    {
        _logger.LogInformation("正在校验本机系统时间...");
        var result = await _systemTimeChecker.CheckAsync(httpProxy);
        if (result.Status != SystemTimeCheckStatus.Inaccurate) return true;

        var direction = result.Offset > TimeSpan.Zero ? "慢" : "快";
        var difference = FormatTimeDifference(result.Offset);
        RuntimeMessage = $"本机系统时间不准确，比网络时间{direction}约 {difference}。请同步系统时间后重试。";
        _logger.LogWarning("阻止启动: {Message}", RuntimeMessage);

        if (ShowAlertDialog != null)
        {
            await ShowAlertDialog(
                "系统时间不准确",
                $"{RuntimeMessage}\n\n请在系统设置中开启自动设置时间，并执行立即同步。");
        }

        return false;
    }

    private static string FormatTimeDifference(TimeSpan offset)
    {
        var difference = offset.Duration();
        if (difference.TotalDays >= 1)
            return $"{difference.TotalDays:0.#} 天";
        if (difference.TotalHours >= 1)
            return $"{difference.TotalHours:0.#} 小时";
        if (difference.TotalMinutes >= 1)
            return $"{difference.TotalMinutes:0.#} 分钟";
        return $"{difference.TotalSeconds:0} 秒";
    }

    private async Task ExecuteStartupCommandAsync(AppConfig config)
    {
        if (!config.StartupCommandEnabled || string.IsNullOrWhiteSpace(config.StartupCommand) || !PlatformHelper.IsWindows)
            return;

        try
        {
            _logger.LogInformation("执行启动后命令: {Command}", config.StartupCommand);
            await Task.Run(() =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start cmd /c \"{config.StartupCommand} & pause\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "执行启动后命令失败");
        }
    }

    // ---- WebView 回报 (由 HomePage 调用) ----

    public void ReportNavigation(bool isSuccess, int webErrorStatus)
    {
        PageError = isSuccess ? string.Empty : $"页面加载失败 (WebErrorStatus {webErrorStatus})，请点击刷新重试";
        if (!isSuccess) _logger.LogWarning("WebUI 页面加载失败, WebErrorStatus={Status}, url={Url}", webErrorStatus, WebUiUrl);
    }

    public void ReportWebViewFailure(Exception ex)
    {
        _logger.LogError(ex, "内嵌 WebView2 初始化失败");
        _webViewFailure = ex.Message;
        WebViewSupported = false;
    }

    public void ClearPageError() => PageError = string.Empty;

    private void SetWebUiClosed(bool closed)
    {
        if (IsWebUiClosed == closed) return;
        _logger.LogInformation("{Action} WebUI 页面", closed ? "关闭" : "重新打开");
        PageError = string.Empty;
        if (closed) ShowTerminal = false;
        IsWebUiClosed = closed;
    }

    // ---- 更新检查 ----

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var appVersion = GetAppVersion();
            var state = new UpdateState { IsChecked = true };

            var appUpdate = await _updateChecker.CheckAppUpdateAsync(appVersion);
            if (appUpdate.HasUpdate)
            {
                state.AppHasUpdate = true;
                state.AppLatestVersion = appUpdate.LatestVersion;
                state.AppReleaseUrl = appUpdate.ReleaseUrl;
                _logger.LogInformation("发现管理器新版本: {Version}", appUpdate.LatestVersion);
            }
            else
            {
                _logger.LogInformation("管理器已是最新版本");
            }

            _updateStateService.UpdateState(state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查更新失败");
        }
    }

    private void OnUpdateStateChanged(UpdateState state)
    {
        HasUpdate = state.HasAnyUpdate;
        if (!state.HasAnyUpdate) return;
        UpdateBannerText = string.IsNullOrEmpty(state.AppLatestVersion)
            ? "发现管理器新版本"
            : $"发现管理器新版本: {state.AppLatestVersion}";
    }

    private static string GetAppVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }

    private void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开失败: {Target}", target);
        }
    }
}
