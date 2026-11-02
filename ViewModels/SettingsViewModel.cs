using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

/// <summary>
/// 设置页: 只有桌面端自己的设置. LLBot 的登录方式 / QQ 路径 / 协议 / WebUI 端口等都在它的 WebUI 里.
/// 开关和下拉框改动即时落盘 (单 key 写); 文本框是草稿, 点"保存"才写, 见 doc/config-persistence.md.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IConfigManager _configManager;

    // 从配置回填时不要触发写回 / 主题回调
    private bool _loading;

    public Func<string, string, Task>? ShowAlertDialog { get; set; }

    // ---- 窗口 ----

    // 主题归 MainWindowViewModel 管 (标题栏按钮也能切), 这里只是另一个入口: 改动通过回调交给它, 它切完再 SyncThemeMode 回来
    public Action<string>? ThemeModeChanged { get; set; }

    private static readonly string[] ThemeModes = ["system", "light", "dark"];

    private int _themeModeIndex;
    public int ThemeModeIndex
    {
        get => _themeModeIndex;
        set
        {
            if (_themeModeIndex == value) return;
            this.RaiseAndSetIfChanged(ref _themeModeIndex, value);
            if (!_loading && value >= 0 && value < ThemeModes.Length) ThemeModeChanged?.Invoke(ThemeModes[value]);
        }
    }

    public void SyncThemeMode(string mode)
    {
        var index = Array.IndexOf(ThemeModes, mode);
        if (index < 0) index = 0;
        _loading = true;
        ThemeModeIndex = index;
        _loading = false;
    }

    private bool _minimizeToTrayOnStart;
    public bool MinimizeToTrayOnStart
    {
        get => _minimizeToTrayOnStart;
        set
        {
            if (_minimizeToTrayOnStart == value) return;
            this.RaiseAndSetIfChanged(ref _minimizeToTrayOnStart, value);
            if (!_loading) _ = _configManager.SetSettingAsync("minimize_to_tray_on_start", value);
        }
    }

    // 0 每次询问 (null) / 1 最小化到托盘 (true) / 2 退出 (false), 见 doc/config-persistence.md
    private int _closeBehaviorIndex;
    public int CloseBehaviorIndex
    {
        get => _closeBehaviorIndex;
        set
        {
            if (_closeBehaviorIndex == value) return;
            this.RaiseAndSetIfChanged(ref _closeBehaviorIndex, value);
            if (_loading) return;
            bool? closeToTray = value switch { 1 => true, 2 => false, _ => null };
            _ = _configManager.SetSettingAsync("close_to_tray", closeToTray);
        }
    }

    // ---- 启动 ----

    public bool IsStartupSupported => PlatformHelper.IsWindows;

    private bool _startupEnabled;
    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (_startupEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _startupEnabled, value);
            if (!_loading) _ = ApplyStartupAsync(value);
        }
    }

    // 启动后命令用 cmd.exe 起, 只有 Windows
    public bool IsStartupCommandSupported => PlatformHelper.IsWindows;

    private bool _startupCommandEnabled;
    public bool StartupCommandEnabled
    {
        get => _startupCommandEnabled;
        set
        {
            if (_startupCommandEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _startupCommandEnabled, value);
            if (!_loading) _ = _configManager.SetSettingAsync("startup_command_enabled", value);
        }
    }

    private string _startupCommand = string.Empty;
    public string StartupCommand
    {
        get => _startupCommand;
        set => SetDraft(ref _startupCommand, value);
    }

    // ---- LLBot 运行环境 (文本是草稿, 点保存才写; 下次启动 LLBot 生效) ----

    private string _nodePath = string.Empty;
    public string NodePath
    {
        get => _nodePath;
        set => SetDraft(ref _nodePath, value);
    }

    private string _llbotPath = string.Empty;
    public string LLBotPath
    {
        get => _llbotPath;
        set => SetDraft(ref _llbotPath, value);
    }

    private string _httpProxy = string.Empty;
    public string HttpProxy
    {
        get => _httpProxy;
        set => SetDraft(ref _httpProxy, value);
    }

    // 0 国外 (overseas, 默认) / 1 国内 (china -> --cdn china)
    private int _serverRegionIndex;
    public int ServerRegionIndex
    {
        get => _serverRegionIndex;
        set
        {
            if (_serverRegionIndex == value) return;
            this.RaiseAndSetIfChanged(ref _serverRegionIndex, value);
            if (!_loading) _ = _configManager.SetSettingAsync("server_region", value == 1 ? "china" : "overseas");
        }
    }

    // 草稿与已保存值的对照, 决定"保存"按钮和未保存提示
    private readonly Dictionary<string, string> _saved = new();

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => this.RaiseAndSetIfChanged(ref _hasUnsavedChanges, value);
    }

    private bool _isSaving;
    public bool IsSaving
    {
        get => _isSaving;
        private set => this.RaiseAndSetIfChanged(ref _isSaving, value);
    }

    private string _saveMessage = string.Empty;
    public string SaveMessage
    {
        get => _saveMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _saveMessage, value);
            this.RaisePropertyChanged(nameof(HasSaveMessage));
        }
    }

    public bool HasSaveMessage => !string.IsNullOrEmpty(SaveMessage);

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseNodeCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseLLBotCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenWorkingDirectoryCommand { get; }

    public SettingsViewModel(ILogger<SettingsViewModel> logger, IConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;

        SaveCommand = ReactiveCommand.Create(() =>
        {
            if (HasUnsavedChanges && !IsSaving) _ = SaveDraftsAsync();
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        BrowseNodeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var picked = await PickFileAsync("选择 Node.js 可执行文件", NodePath, PlatformHelper.IsWindows ? "node.exe" : "node");
            if (picked != null) NodePath = picked;
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        BrowseLLBotCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var picked = await PickFileAsync("选择 LLBot 脚本 (llbot.js)", LLBotPath, "llbot.js");
            if (picked != null) LLBotPath = picked;
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        OpenLogsCommand = ReactiveCommand.Create(() => OpenPath(Path.GetFullPath("logs")), outputScheduler: AvaloniaUiScheduler.Instance);
        OpenWorkingDirectoryCommand = ReactiveCommand.Create(() => OpenPath(Environment.CurrentDirectory), outputScheduler: AvaloniaUiScheduler.Instance);

        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();
            var startupEnabled = IsStartupSupported && await Task.Run(StartupManager.IsStartupEnabled);
            _loading = true;
            MinimizeToTrayOnStart = config.MinimizeToTrayOnStart;
            CloseBehaviorIndex = config.CloseToTray switch { true => 1, false => 2, null => 0 };
            StartupEnabled = startupEnabled;
            StartupCommandEnabled = config.StartupCommandEnabled;
            ServerRegionIndex = config.ServerRegion == "china" ? 1 : 0;
            StartupCommand = config.StartupCommand;
            NodePath = config.NodePath;
            LLBotPath = config.LLBotPath;
            HttpProxy = config.HttpProxy;
            MarkSaved();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取桌面端设置失败");
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetDraft(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(propertyName);
        if (!_loading) UpdateDirty();
    }

    private void MarkSaved()
    {
        _saved[nameof(StartupCommand)] = StartupCommand;
        _saved[nameof(NodePath)] = NodePath;
        _saved[nameof(LLBotPath)] = LLBotPath;
        _saved[nameof(HttpProxy)] = HttpProxy;
        HasUnsavedChanges = false;
    }

    private void UpdateDirty()
    {
        HasUnsavedChanges = _saved.GetValueOrDefault(nameof(StartupCommand)) != StartupCommand
            || _saved.GetValueOrDefault(nameof(NodePath)) != NodePath
            || _saved.GetValueOrDefault(nameof(LLBotPath)) != LLBotPath
            || _saved.GetValueOrDefault(nameof(HttpProxy)) != HttpProxy;
        if (HasUnsavedChanges) SaveMessage = string.Empty;
    }

    private async Task SaveDraftsAsync()
    {
        IsSaving = true;
        try
        {
            NodePath = NodePath.Trim();
            LLBotPath = LLBotPath.Trim();
            HttpProxy = HttpProxy.Trim();
            StartupCommand = StartupCommand.Trim();

            await _configManager.SetSettingAsync("node_path", NodePath);
            await _configManager.SetSettingAsync("llbot_path", LLBotPath);
            await _configManager.SetSettingAsync("http_proxy", HttpProxy);
            await _configManager.SetSettingAsync("startup_command", StartupCommand);
            MarkSaved();
            SaveMessage = "已保存，下次启动 LLBot 时生效";
            _logger.LogInformation("桌面端设置已保存");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存桌面端设置失败");
            SaveMessage = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task ApplyStartupAsync(bool enable)
    {
        var result = await Task.Run(() => enable ? StartupManager.TryEnableStartup() : StartupManager.TryDisableStartup());
        if (result.Success)
        {
            _logger.LogInformation("开机自启已{State}", enable ? "开启" : "关闭");
            return;
        }

        _logger.LogWarning("{Action}开机自启失败: {Error}", enable ? "开启" : "关闭", result.ErrorMessage);
        _loading = true;
        StartupEnabled = !enable;
        _loading = false;
        if (ShowAlertDialog != null)
            await ShowAlertDialog("开机自启", $"{(enable ? "开启" : "关闭")}开机自启失败：{result.ErrorMessage}");
    }

    private async Task<string?> PickFileAsync(string title, string currentPath, string fileName)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                return null;

            var options = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(fileName) { Patterns = [fileName] },
                    new FilePickerFileType("所有文件") { Patterns = ["*"] },
                ],
            };
            var currentDir = Path.GetDirectoryName(string.IsNullOrWhiteSpace(currentPath) ? null : Path.GetFullPath(currentPath));
            if (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
                options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(currentDir);

            var result = await window.StorageProvider.OpenFilePickerAsync(options);
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "选择文件失败");
            return null;
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开目录失败: {Path}", path);
        }
    }
}
