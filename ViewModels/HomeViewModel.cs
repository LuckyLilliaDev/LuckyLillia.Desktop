using Avalonia.Threading;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using LuckyLilliaDesktop.Utils.WebView2;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

/// <summary>运行环境 (Node.js / LLBot / FFmpeg) 的准备阶段, 所有实例共用.</summary>
public enum RuntimePhase
{
    Idle,
    Preparing,
    Downloading,
    Ready,
    Failed
}

/// <summary>
/// 控制面板: 管理 LLBot 实例 (data/instances/&lt;实例名&gt;, 每个实例一个进程), 把选中实例的 WebUI 嵌在 WebView2 里.
/// LLBot 自己的配置 / 登录 / WebUI 密码全在 WebUI 里处理; Desktop 做运行时准备 -> 分配端口并启动进程 -> 等 WebUI 就绪 -> 导航.
/// 见 doc/llbot-webui.md.
/// </summary>
public class HomeViewModel : ViewModelBase
{
    private static readonly TimeSpan WebUiReadyTimeout = TimeSpan.FromSeconds(90);
    // 启动门槛: LLBot 主版本必须 >= 8 (实例配置 / WebUI 登录页都是 8 才有的)
    private const int MinMajorVersion = 8;

    private readonly IProcessManager _processManager;
    private readonly IConfigManager _configManager;
    private readonly ISystemTimeChecker _systemTimeChecker;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateChecker _updateChecker;
    private readonly IUpdateStateService _updateStateService;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    // 实例的启动 / 停止串行化: 端口分配要看别的实例占了哪些
    private readonly SemaphoreSlim _instanceGate = new(1, 1);
    private CancellationTokenSource? _downloadCts;
    private AppConfig _config = AppConfig.Default;
    private bool _startupCommandDone;
    private List<string> _resumeAfterUpdate = [];

    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    public Func<string, string, Task>? ShowAlertDialog { get; set; }
    public Func<string, Func<Task>, Task>? ShowLoadingDialog { get; set; }
    /// <summary>(标题, 说明, 水印) -> 输入的文本, 取消为 null</summary>
    public Func<string, string, string, Task<string?>>? ShowTextInputDialog { get; set; }
    public Action? NavigateToAbout { get; set; }
    public Func<Task>? OnDownloadCompleted { get; set; }

    // ---- 实例 ----

    public ObservableCollection<InstanceViewModel> Instances { get; } = new();

    private InstanceViewModel? _selectedInstance;
    public InstanceViewModel? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (ReferenceEquals(_selectedInstance, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedInstance, value);
            this.RaisePropertyChanged(nameof(PickerSelection));
            TerminalLog.Instance = value?.Name;
            PageError = string.Empty;
            RaiseDerived();
        }
    }

    // 工具条下拉框的内容: 所有实例 + 末尾的 "新建实例" 项
    public ObservableCollection<object> PickerItems { get; } = new();

    /// <summary>选中实例的终端输出 (stdout / stderr), 工具条 "终端日志" 切换后顶替 WebView 显示.</summary>
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

    // 下拉框把 null (项被移除时) 和 "新建实例" 项推过来都不当选择; "新建实例" 由 HomePage 把下拉框拨回当前实例后调 RequestNewInstance
    public object? PickerSelection
    {
        get => SelectedInstance;
        set
        {
            if (value is InstanceViewModel vm) SelectedInstance = vm;
        }
    }

    public void RequestNewInstance() => _ = NewInstanceAsync();

    // 不整表重建: 清空会让下拉框丢掉当前选中项, 而两向绑定把同一个值再推回去时不会生效, 框就空了
    private void SyncPickerItems()
    {
        var desired = new List<object>(Instances.Count + 1);
        desired.AddRange(Instances);
        desired.Add(NewInstanceEntry.Instance);
        for (var i = PickerItems.Count - 1; i >= 0; i--)
            if (!desired.Contains(PickerItems[i])) PickerItems.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var current = PickerItems.IndexOf(desired[i]);
            if (current == i) continue;
            if (current >= 0) PickerItems.Move(current, i);
            else PickerItems.Insert(i, desired[i]);
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

    // ---- 派生状态 (选中实例 + 运行环境) ----

    public bool HasInstances => Instances.Count > 0;
    public bool IsRuntimeReady => RuntimePhase == RuntimePhase.Ready;
    public bool IsSelectedRunning => SelectedInstance?.Phase == InstancePhase.Running;
    public bool ShowTerminalView => ShowTerminal && SelectedInstance != null;
    public bool CanShowTerminal => SelectedInstance != null;
    public bool ShowWebView => IsRuntimeReady && IsSelectedRunning && WebViewSupported && !ShowTerminalView;
    public bool ShowOverlay => !ShowWebView && !ShowTerminalView;
    public bool ShowSpinner => RuntimePhase == RuntimePhase.Preparing || SelectedInstance?.IsBusy == true;
    public bool ShowProgress => RuntimePhase == RuntimePhase.Downloading;
    public bool ShowCancelDownload => RuntimePhase == RuntimePhase.Downloading;
    public bool ShowStartButton => RuntimePhase == RuntimePhase.Failed
        || (IsRuntimeReady && SelectedInstance is { Phase: InstancePhase.Stopped or InstancePhase.Failed });
    public string StartButtonText => RuntimePhase == RuntimePhase.Failed || SelectedInstance?.Phase == InstancePhase.Failed ? "重试" : "启动实例";
    public bool ShowNewInstanceButton => IsRuntimeReady && SelectedInstance == null;
    public bool ShowOpenInBrowser => IsSelectedRunning && !WebViewSupported;
    public bool ShowInstallWebView2 => IsSelectedRunning && !WebViewSupported && OperatingSystem.IsWindows() && string.IsNullOrEmpty(_webViewFailure);
    public bool ShowLogsButton => RuntimePhase == RuntimePhase.Failed || SelectedInstance is { Phase: InstancePhase.Stopped or InstancePhase.Failed };
    public bool ShowErrorIcon => RuntimePhase == RuntimePhase.Failed
        || SelectedInstance?.Phase == InstancePhase.Failed
        || SelectedInstance is { Phase: InstancePhase.Stopped, ExitCode: not (null or 0) };
    public bool CanStartStop => RuntimePhase == RuntimePhase.Failed
        || (IsRuntimeReady && SelectedInstance is { Phase: InstancePhase.Running or InstancePhase.Stopped or InstancePhase.Failed });
    public string StartStopText => IsSelectedRunning ? "停止" : "启动";
    public bool CanRestart => IsSelectedRunning;
    public bool CanRefresh => ShowWebView;
    public bool CanDelete => IsRuntimeReady && SelectedInstance is { Phase: InstancePhase.Stopped or InstancePhase.Failed };
    public bool CanToggleAutoStart => SelectedInstance != null;
    public bool SelectedIsAutoStart => SelectedInstance?.IsAutoStart ?? false;
    public bool HasWebUiUrl => IsSelectedRunning && !string.IsNullOrEmpty(SelectedInstance?.Url);
    public string? WebUiUrl => HasWebUiUrl ? SelectedInstance!.Url : null;
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
            var vm = SelectedInstance;
            if (vm == null) return "还没有实例";
            return vm.Phase switch
            {
                InstancePhase.Starting => $"正在启动实例 {vm.Name}",
                InstancePhase.Stopping => $"正在停止实例 {vm.Name}",
                InstancePhase.Failed => $"实例 {vm.Name} 启动失败",
                InstancePhase.Running when !string.IsNullOrEmpty(_webViewFailure) => "内嵌 WebView 初始化失败",
                InstancePhase.Running when OperatingSystem.IsWindows() => "未安装 WebView2 运行时",
                InstancePhase.Running => "当前系统不支持内嵌 WebView",
                _ => $"实例 {vm.Name} 未运行"
            };
        }
    }

    public string OverlayMessage
    {
        get
        {
            switch (RuntimePhase)
            {
                case RuntimePhase.Preparing: return "检查 Node.js、LLBot 与 FFmpeg，缺失的组件会自动下载";
                case RuntimePhase.Downloading: return string.Empty;
                case RuntimePhase.Failed: return RuntimeMessage;
            }
            var vm = SelectedInstance;
            if (vm == null) return "每个实例是一套独立的 LLBot 配置、插件和 WebUI，可以同时运行多个。点击「新建实例」开始。";
            return vm.Phase switch
            {
                InstancePhase.Starting => vm.Port is int port ? $"等待 WebUI 就绪 (端口 {port})" : string.Empty,
                InstancePhase.Running => string.IsNullOrEmpty(_webViewFailure)
                    ? $"可以先在系统浏览器中打开 WebUI: {vm.Url}"
                    : $"{_webViewFailure}\n可以先在系统浏览器中打开 WebUI: {vm.Url}",
                _ => vm.Message
            };
        }
    }

    public bool HasOverlayMessage => !string.IsNullOrEmpty(OverlayMessage);

    private static readonly string[] DerivedProperties =
    [
        nameof(HasInstances), nameof(IsRuntimeReady), nameof(IsSelectedRunning), nameof(ShowWebView), nameof(ShowOverlay),
        nameof(ShowTerminalView), nameof(CanShowTerminal),
        nameof(ShowSpinner), nameof(ShowProgress), nameof(ShowCancelDownload), nameof(ShowStartButton), nameof(StartButtonText),
        nameof(ShowNewInstanceButton), nameof(ShowOpenInBrowser), nameof(ShowInstallWebView2), nameof(ShowLogsButton),
        nameof(ShowErrorIcon), nameof(CanStartStop), nameof(StartStopText), nameof(CanRestart), nameof(CanRefresh),
        nameof(CanDelete), nameof(CanToggleAutoStart), nameof(SelectedIsAutoStart), nameof(HasWebUiUrl), nameof(WebUiUrl),
        nameof(OverlayTitle), nameof(OverlayMessage), nameof(HasOverlayMessage)
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
    public ReactiveCommand<Unit, Unit> NewInstanceCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteInstanceCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAutoStartCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshInstancesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenInBrowserCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallWebView2Command { get; }
    public ReactiveCommand<Unit, Unit> CancelDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogsCommand { get; }

    public HomeViewModel(
        IProcessManager processManager,
        IConfigManager configManager,
        ISystemTimeChecker systemTimeChecker,
        IDownloadService downloadService,
        IUpdateChecker updateChecker,
        IUpdateStateService updateStateService,
        LogViewModel terminalLog,
        ILogger<HomeViewModel> logger)
    {
        TerminalLog = terminalLog;
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
        Instances.CollectionChanged += (_, _) =>
        {
            SyncPickerItems();
            RaiseDerived();
        };
        SyncPickerItems();

        // 按钮启用状态由属性绑定控制, 不用 canExecute observable (ReactiveCommand 的 CanExecuteChanged 会跨线程触发)
        StartStopCommand = ReactiveCommand.Create(() =>
        {
            if (!CanStartStop) return;
            if (RuntimePhase == RuntimePhase.Failed) _ = RetryRuntimeAsync();
            else if (SelectedInstance is { } vm) _ = vm.IsRunning ? StopInstanceAsync(vm) : StartInstanceAsync(vm);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        RestartCommand = ReactiveCommand.Create(() =>
        {
            if (CanRestart && SelectedInstance is { } vm) _ = RestartInstanceAsync(vm);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        NewInstanceCommand = ReactiveCommand.Create(() => { _ = NewInstanceAsync(); }, outputScheduler: AvaloniaUiScheduler.Instance);
        DeleteInstanceCommand = ReactiveCommand.Create(() =>
        {
            if (CanDelete && SelectedInstance is { } vm) _ = DeleteInstanceAsync(vm);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        ToggleAutoStartCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedInstance is { } vm) _ = SetAutoStartAsync(vm, !vm.IsAutoStart);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        RefreshInstancesCommand = ReactiveCommand.Create(() => LoadInstances(), outputScheduler: AvaloniaUiScheduler.Instance);
        OpenInBrowserCommand = ReactiveCommand.Create(() =>
        {
            if (HasWebUiUrl) OpenExternal(WebUiUrl!);
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        InstallWebView2Command = ReactiveCommand.Create(() => OpenExternal(WebView2Runtime.InstallerUrl), outputScheduler: AvaloniaUiScheduler.Instance);
        CancelDownloadCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("用户取消下载");
            _downloadCts?.Cancel();
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        UpdateCommand = ReactiveCommand.Create(() => NavigateToAbout?.Invoke(), outputScheduler: AvaloniaUiScheduler.Instance);
        ViewLogsCommand = ReactiveCommand.Create(() => { ShowTerminal = true; }, outputScheduler: AvaloniaUiScheduler.Instance);
        OpenLogsCommand = ReactiveCommand.Create(() => OpenExternal(Path.GetFullPath("logs")), outputScheduler: AvaloniaUiScheduler.Instance);

        _ = CheckForUpdatesAsync();
        _ = InitializeAsync();
    }

    // ---- 启动流程 ----

    private async Task InitializeAsync()
    {
        try
        {
            _config = await _configManager.LoadConfigAsync();
            LoadInstances();
            // 让主窗口先画出来
            await Task.Delay(300);
            if (!await EnsureRuntimeAsync()) return;
            await StartAutoStartInstancesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "控制面板初始化失败");
        }
    }

    private async Task StartAutoStartInstancesAsync()
    {
        foreach (var vm in Instances.Where(i => i.IsAutoStart).ToList())
        {
            await StartInstanceAsync(vm);
        }
    }

    private async Task RetryRuntimeAsync()
    {
        RuntimePhase = RuntimePhase.Idle;
        if (!await EnsureRuntimeAsync()) return;
        await StartAutoStartInstancesAsync();
    }

    /// <summary>扫描 data/instances 重建实例列表, 保留已有实例对象的运行状态.</summary>
    public void LoadInstances()
    {
        var names = LLBotWebUi.ListInstances(_config.LLBotPath);
        if (!names.Contains(LLBotWebUi.DefaultInstance)) names.Insert(0, LLBotWebUi.DefaultInstance);
        // 正在跑的实例目录被外部删了也不能从列表里消失
        foreach (var running in _processManager.RunningInstances)
            if (!names.Contains(running)) names.Add(running);

        var autoStart = new HashSet<string>(_config.AutoStartInstances, StringComparer.Ordinal);

        // 就地增删改序, 已有的实例对象 (含运行状态和下拉框的选中项) 原样保留
        for (var i = Instances.Count - 1; i >= 0; i--)
        {
            if (names.Contains(Instances[i].Name)) continue;
            Instances[i].PropertyChanged -= OnInstancePropertyChanged;
            Instances.RemoveAt(i);
        }
        for (var i = 0; i < names.Count; i++)
        {
            var current = -1;
            for (var j = 0; j < Instances.Count; j++)
            {
                if (Instances[j].Name != names[i]) continue;
                current = j;
                break;
            }
            if (current < 0)
            {
                var vm = new InstanceViewModel(names[i]);
                vm.PropertyChanged += OnInstancePropertyChanged;
                Instances.Insert(i, vm);
            }
            else if (current != i)
            {
                Instances.Move(current, i);
            }
            Instances[i].IsAutoStart = autoStart.Contains(names[i]);
        }

        if (SelectedInstance == null || !Instances.Contains(SelectedInstance))
            SelectedInstance = Instances.FirstOrDefault(i => i.IsAutoStart) ?? Instances.FirstOrDefault();
        RaiseDerived();
        _logger.LogInformation("实例列表: {Instances} (自动启动: {AutoStart})", string.Join(", ", names), string.Join(", ", autoStart));
    }

    private void OnInstancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedInstance)) RaiseDerived();
    }

    public async Task StartInstanceAsync(InstanceViewModel vm)
    {
        if (vm.Phase is not (InstancePhase.Stopped or InstancePhase.Failed)) return;
        if (!await EnsureRuntimeAsync()) return;

        var config = _config;
        var gateHeld = false;
        try
        {
            // 从分配端口到进程起来这一段串行, 同时启动的实例才看得见彼此占的端口; 等 WebUI 就绪不占锁
            await _instanceGate.WaitAsync();
            gateHeld = true;
            if (vm.Phase is not (InstancePhase.Stopped or InstancePhase.Failed)) return;

            vm.Phase = InstancePhase.Starting;
            vm.Message = string.Empty;
            vm.ExitCode = null;
            vm.Url = null;
            vm.Port = null;
            if (ReferenceEquals(vm, SelectedInstance)) PageError = string.Empty;

            var endpoint = LLBotWebUi.ReadEndpoint(config.LLBotPath, vm.Name);
            if (!endpoint.Enabled)
            {
                Fail(vm, $"WebUI 已在该实例的配置中关闭 ({LLBotWebUi.SettingsPath(config.LLBotPath, vm.Name)} 的 webui.enable)，请改回 true 后重试");
                return;
            }

            // 端口: 实例配置里的是首选, 被占 (别的实例 / 别的程序) 就往上顺延, 以 --webui-port 传给 LLBot
            var used = Instances.Where(i => !ReferenceEquals(i, vm) && i.Port is int).Select(i => i.Port!.Value).ToHashSet();
            var free = TcpPorts.FindFree(endpoint.Port, used);
            if (free == null)
            {
                Fail(vm, $"从端口 {endpoint.Port} 起找不到空闲端口");
                return;
            }
            var port = free.Value;
            if (port != endpoint.Port)
                _logger.LogWarning("实例 {Instance}: 配置端口 {Configured} 被占用, 改用 {Port}", vm.Name, endpoint.Port, port);
            vm.Port = port;

            var started = await _processManager.StartInstanceAsync(vm.Name,
                new LLBotLaunchOptions(config.NodePath, config.LLBotPath, port, config.HttpProxy, config.ServerRegion == "china"));
            if (!started)
            {
                var code = _processManager.GetExitCode(vm.Name);
                vm.ExitCode = code;
                Fail(vm, code is null or 0 ? "LLBot 启动失败，请查看日志" : $"LLBot 启动失败 (退出码 {code})，请查看日志");
                return;
            }
            vm.Pid = _processManager.GetPid(vm.Name);

            _instanceGate.Release();
            gateHeld = false;

            var url = await WaitForWebUiAsync(vm, endpoint.ConnectHost, port);
            // 等待期间被停掉 / 进程自己退出: 状态已由别处填好
            if (vm.Phase != InstancePhase.Starting) return;
            if (url == null)
            {
                await StopQuietlyAsync(vm);
                Fail(vm, $"等待 WebUI 就绪超时 ({WebUiReadyTimeout.TotalSeconds:0} 秒)，请查看日志");
                return;
            }

            vm.Url = url;
            vm.Phase = InstancePhase.Running;
            _logger.LogInformation("实例 {Instance} 的 WebUI 已就绪: {Url}", vm.Name, url);

            if (!_startupCommandDone)
            {
                _startupCommandDone = true;
                await ExecuteStartupCommandAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动实例 {Instance} 时出错", vm.Name);
            if (vm.Phase == InstancePhase.Starting)
            {
                await StopQuietlyAsync(vm);
                Fail(vm, $"启动实例时出错: {ex.Message}");
            }
        }
        finally
        {
            if (gateHeld) _instanceGate.Release();
        }
    }

    public async Task StopInstanceAsync(InstanceViewModel vm)
    {
        if (vm.Phase is not (InstancePhase.Running or InstancePhase.Starting)) return;
        vm.Phase = InstancePhase.Stopping;
        _logger.LogInformation("停止实例 {Instance}...", vm.Name);
        try
        {
            if (ShowLoadingDialog != null)
                await ShowLoadingDialog($"正在停止实例 {vm.Name}...", () => _processManager.StopInstanceAsync(vm.Name));
            else
                await _processManager.StopInstanceAsync(vm.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止实例 {Instance} 时出错", vm.Name);
        }
        vm.ExitCode = _processManager.GetExitCode(vm.Name) ?? 0;
        vm.Url = null;
        vm.Pid = null;
        vm.Port = null;
        vm.Message = "已手动停止";
        vm.Phase = InstancePhase.Stopped;
    }

    private async Task RestartInstanceAsync(InstanceViewModel vm)
    {
        if (ConfirmDialog != null && !await ConfirmDialog("重启实例", $"确定要重启实例 {vm.Name} 吗？它的登录会话会断开。"))
            return;
        await StopInstanceAsync(vm);
        await StartInstanceAsync(vm);
    }

    /// <summary>关于页更新 LLBot 前调用: 停掉所有实例, 记住哪些在跑.</summary>
    public async Task StopAllForUpdateAsync()
    {
        _resumeAfterUpdate = Instances.Where(i => i.IsRunning || i.Phase == InstancePhase.Starting).Select(i => i.Name).ToList();
        foreach (var vm in Instances.Where(i => i.Phase is InstancePhase.Running or InstancePhase.Starting).ToList())
            await StopInstanceAsync(vm);
    }

    /// <summary>关于页更新 LLBot 后调用: 重新过一遍运行环境检查 (版本变了), 再把更新前在跑的实例拉起来.</summary>
    public async Task ResumeAfterUpdateAsync()
    {
        RuntimePhase = RuntimePhase.Idle;
        if (!await EnsureRuntimeAsync()) return;
        foreach (var name in _resumeAfterUpdate)
        {
            var vm = Instances.FirstOrDefault(i => i.Name == name);
            if (vm != null) await StartInstanceAsync(vm);
        }
        _resumeAfterUpdate = [];
    }

    private async Task NewInstanceAsync()
    {
        if (ShowTextInputDialog == null) return;
        var input = await ShowTextInputDialog("新建实例",
            "实例名只能用字母、数字、_ 和 -，以字母或数字开头，最长 64 个字符。LLBot 会用默认配置创建它。",
            "例如 bot2");
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (!LLBotWebUi.IsValidInstanceName(name))
        {
            if (ShowAlertDialog != null) await ShowAlertDialog("实例名不合法", "实例名只能用字母、数字、_ 和 -，以字母或数字开头，最长 64 个字符。");
            return;
        }
        if (Instances.Any(i => i.Name == name))
        {
            if (ShowAlertDialog != null) await ShowAlertDialog("实例已存在", $"实例 {name} 已经在列表里了。");
            SelectedInstance = Instances.First(i => i.Name == name);
            return;
        }

        var vm = new InstanceViewModel(name);
        vm.PropertyChanged += OnInstancePropertyChanged;
        Instances.Add(vm);
        SelectedInstance = vm;
        _logger.LogInformation("新建实例 {Instance}", name);
        // LLBot 第一次以这个实例名启动时会按 default_config.json 建出实例目录
        await StartInstanceAsync(vm);
    }

    private async Task DeleteInstanceAsync(InstanceViewModel vm)
    {
        if (vm.Phase is not (InstancePhase.Stopped or InstancePhase.Failed)) return;
        var dir = LLBotWebUi.InstanceDir(_config.LLBotPath, vm.Name);
        if (ConfirmDialog != null && !await ConfirmDialog("删除实例",
                $"确定删除实例 {vm.Name} 吗？\n\n会删除它的配置目录 {dir}（设置、插件配置与数据、插件数据库、WebUI 密码）。QQ 账号自己的消息数据库在 data/database 下，不会被删。"))
            return;

        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var holder = LLBotWebUi.LockHolderPath(_config.LLBotPath, vm.Name);
            if (File.Exists(holder)) File.Delete(holder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除实例 {Instance} 失败", vm.Name);
            if (ShowAlertDialog != null) await ShowAlertDialog("删除失败", ex.Message);
            return;
        }

        vm.PropertyChanged -= OnInstancePropertyChanged;
        Instances.Remove(vm);
        if (vm.IsAutoStart) await SaveAutoStartAsync();
        SelectedInstance = Instances.FirstOrDefault(i => i.Name == LLBotWebUi.DefaultInstance) ?? Instances.FirstOrDefault();
        _logger.LogInformation("已删除实例 {Instance}", vm.Name);
    }

    private async Task SetAutoStartAsync(InstanceViewModel vm, bool enabled)
    {
        vm.IsAutoStart = enabled;
        RaiseDerived();
        await SaveAutoStartAsync();
    }

    private async Task SaveAutoStartAsync()
    {
        var names = Instances.Where(i => i.IsAutoStart).Select(i => i.Name).ToList();
        _config.AutoStartInstances = names;
        await _configManager.SetSettingAsync("auto_start_instances", names);
    }

    private void Fail(InstanceViewModel vm, string message)
    {
        _logger.LogWarning("实例 {Instance} 启动中止: {Message}", vm.Name, message);
        vm.Message = message;
        vm.Url = null;
        vm.Pid = null;
        vm.Port = null;
        vm.Phase = InstancePhase.Failed;
    }

    private async Task StopQuietlyAsync(InstanceViewModel vm)
    {
        try { await _processManager.StopInstanceAsync(vm.Name); }
        catch (Exception ex) { _logger.LogWarning(ex, "清理实例 {Instance} 进程失败", vm.Name); }
    }

    private void OnProcessStatusChanged(object? sender, InstanceStatusChangedEventArgs e)
    {
        if (e.Status is not (ProcessStatus.Stopped or ProcessStatus.Error)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var vm = Instances.FirstOrDefault(i => i.Name == e.Instance);
            if (vm is { Phase: InstancePhase.Running or InstancePhase.Starting }) MarkExited(vm);
        });
    }

    private void MarkExited(InstanceViewModel vm)
    {
        var code = _processManager.GetExitCode(vm.Name);
        vm.ExitCode = code;
        vm.Message = code is null or 0 ? "LLBot 进程已退出" : $"LLBot 进程异常退出 (退出码 {code})";
        _logger.LogWarning("实例 {Instance}: {Message}", vm.Name, vm.Message);
        vm.Url = null;
        vm.Pid = null;
        vm.Port = null;
        vm.Phase = InstancePhase.Stopped;
    }

    // ---- WebUI 就绪 ----

    // 端口能连上还不够: 要确认监听它的就是这个实例的进程 (Windows 查 TCP 表), 别的程序或别的实例占着同一个端口时
    // 连通探测会被骗. LLBot 拿到 --webui-port 后发现端口被占会自己往后顺延 (旧版本则完全不认这个参数),
    // 那就在它监听的端口里找带 WebUI 特征的.
    // 返回 null: 超时, 进程中途退出 (已 MarkExited), 或等待期间实例被停掉.
    private async Task<string?> WaitForWebUiAsync(InstanceViewModel vm, string host, int port)
    {
        var deadline = DateTime.UtcNow + WebUiReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (vm.Phase != InstancePhase.Starting) return null;
            if (_processManager.GetStatus(vm.Name) != ProcessStatus.Running)
            {
                MarkExited(vm);
                return null;
            }

            var pid = _processManager.GetPid(vm.Name);
            var listeners = TcpListenerTable.GetListeners();
            if (listeners != null && pid is int ownPid)
            {
                var owned = listeners.Where(l => l.Pid == ownPid).Select(l => l.Port).Distinct().ToList();
                if (owned.Contains(port)) return LLBotWebUi.Url(host, port);
                foreach (var candidate in owned.Where(c => c != port))
                {
                    if (!await LLBotWebUi.IsWebUiAsync(host, candidate)) continue;
                    _logger.LogWarning("实例 {Instance}: LLBot 没有采用分配的端口 {Port}, 实际监听 {Actual}", vm.Name, port, candidate);
                    vm.Port = candidate;
                    return LLBotWebUi.Url(host, candidate);
                }
            }
            else if (await LLBotWebUi.IsWebUiAsync(host, port))
            {
                return LLBotWebUi.Url(host, port);
            }

            await Task.Delay(500);
        }
        return null;
    }

    // ---- 运行时准备 ----

    private async Task<bool> EnsureRuntimeAsync()
    {
        if (RuntimePhase == RuntimePhase.Ready) return true;
        await _runtimeGate.WaitAsync();
        try
        {
            if (RuntimePhase == RuntimePhase.Ready) return true;
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

            if (!FFmpegHelper.CheckFFmpegExists() || !FFmpegHelper.CheckFFprobeExists())
            {
                if (!await DownloadAsync("FFmpeg", _downloadService.DownloadFFmpegAsync)) return false;
            }

            RuntimePhase = RuntimePhase.Preparing;
            if (configChanged)
            {
                await _configManager.SaveConfigAsync(config);
                if (OnDownloadCompleted != null) await OnDownloadCompleted();
            }

            if (!File.Exists(config.NodePath)) return RuntimeFailed($"Node.js 文件不存在: {config.NodePath}");
            if (!File.Exists(config.LLBotPath)) return RuntimeFailed($"LLBot 脚本不存在: {config.LLBotPath}");
            if (!FFmpegHelper.CheckFFmpegExists() || !FFmpegHelper.CheckFFprobeExists()) return RuntimeFailed("FFmpeg / FFprobe 不可用");

            var versionError = CheckVersionRequirement(config);
            if (versionError != null) return RuntimeFailed(versionError);

            RuntimePhase = RuntimePhase.Ready;
            LoadInstances();
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

    // 取主版本号 (major). 兼容 "8.0.1" / "v8.0.1" / "8.0.0-beta". 解析不出返回 null.
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

    // ---- 更新检查 ----

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();
            var appVersion = GetAppVersion();
            var llbotVersion = VersionDetector.DetectLLBotVersion(config.LLBotPath, _logger);

            var updateNames = new List<string>();
            var state = new UpdateState { IsChecked = true };

            var appUpdate = await _updateChecker.CheckAppUpdateAsync(appVersion);
            if (appUpdate.HasUpdate)
            {
                updateNames.Add("管理器");
                state.AppHasUpdate = true;
                state.AppLatestVersion = appUpdate.LatestVersion;
                state.AppReleaseUrl = appUpdate.ReleaseUrl;
                _logger.LogInformation("发现管理器新版本: {Version}", appUpdate.LatestVersion);
            }

            if (!string.IsNullOrEmpty(llbotVersion))
            {
                var llbotUpdate = await _updateChecker.CheckLLBotUpdateAsync(llbotVersion);
                if (llbotUpdate.HasUpdate)
                {
                    updateNames.Add("LLBot");
                    state.LLBotHasUpdate = true;
                    state.LLBotLatestVersion = llbotUpdate.LatestVersion;
                    state.LLBotReleaseUrl = llbotUpdate.ReleaseUrl;
                    _logger.LogInformation("发现 LLBot 新版本: {Version}", llbotUpdate.LatestVersion);
                }
            }

            _updateStateService.UpdateState(state);
            if (updateNames.Count == 0) _logger.LogInformation("所有组件已是最新版本");
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
        var names = new List<string>();
        if (state.AppHasUpdate) names.Add("管理器");
        if (state.LLBotHasUpdate) names.Add("LLBot");
        UpdateBannerText = $"发现新版本: {string.Join(", ", names)}";
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
