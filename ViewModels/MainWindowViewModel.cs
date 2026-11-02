using ReactiveUI;
using Avalonia;
using Avalonia.Styling;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Reactive;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel: 三个页面 (0 控制面板, 1 设置, 2 关于) + 主题切换.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IConfigManager _configManager;

    private int _selectedIndex;
    private string _themeMode = "system";

    public string Title => Constants.AppDisplayName;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    public string ThemeMode
    {
        get => _themeMode;
        set
        {
            if (_themeMode == value) return;
            this.RaiseAndSetIfChanged(ref _themeMode, value);
            this.RaisePropertyChanged(nameof(IsSystemTheme));
            this.RaisePropertyChanged(nameof(IsDarkTheme));
            this.RaisePropertyChanged(nameof(IsLightTheme));
            this.RaisePropertyChanged(nameof(ThemeToggleTooltip));
        }
    }

    public bool IsSystemTheme => ThemeMode == "system";
    public bool IsDarkTheme => ThemeMode == "dark";
    public bool IsLightTheme => ThemeMode == "light";
    public string ThemeToggleTooltip => ThemeMode switch
    {
        "system" => "主题：跟随系统，点击切换为深色",
        "dark" => "主题：深色，点击切换为浅色",
        "light" => "主题：浅色，点击切换为跟随系统",
        _ => "切换主题"
    };

    public HomeViewModel HomeVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public AboutViewModel AboutVM { get; }

    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }

    public MainWindowViewModel(
        HomeViewModel homeViewModel,
        SettingsViewModel settingsViewModel,
        AboutViewModel aboutViewModel,
        IConfigManager configManager,
        ILogger<MainWindowViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

        HomeVM = homeViewModel ?? throw new ArgumentNullException(nameof(homeViewModel));
        SettingsVM = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));
        AboutVM = aboutViewModel ?? throw new ArgumentNullException(nameof(aboutViewModel));

        _ = LoadThemeSettingsAsync();

        ToggleThemeCommand = ReactiveCommand.Create(ToggleTheme, outputScheduler: AvaloniaUiScheduler.Instance);

        // 设置页的主题下拉框是第二个入口, 切完同步回去; 标题栏按钮切换时也同步给它
        settingsViewModel.ThemeModeChanged = mode =>
        {
            ThemeMode = NormalizeThemeMode(mode);
            ApplyTheme();
            _ = _configManager.SetSettingAsync("theme_mode", ThemeMode);
            _logger.LogInformation("主题切换为: {Theme}", ThemeMode);
        };

        homeViewModel.NavigateToAbout = () => SelectedIndex = 2;
        homeViewModel.OnDownloadCompleted = async () => await AboutVM.LoadVersionsAsync();

        // 更新 LLBot 前后由控制面板统一停 / 起所有实例, 保持它们的状态一致
        aboutViewModel.StopServicesCallback = () => HomeVM.StopAllForUpdateAsync();
        aboutViewModel.RestartServicesCallback = () => HomeVM.ResumeAfterUpdateAsync();

        _logger.LogInformation("MainWindowViewModel 已初始化");
    }

    private async Task LoadThemeSettingsAsync()
    {
        try
        {
            await _configManager.LoadConfigAsync();
            ThemeMode = NormalizeThemeMode(_configManager.GetSetting("theme_mode", "system"));
            ApplyTheme();
            SettingsVM.SyncThemeMode(ThemeMode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载主题设置失败");
        }
    }

    private void ToggleTheme()
    {
        ThemeMode = ThemeMode switch
        {
            "system" => "dark",
            "dark" => "light",
            _ => "system"
        };

        ApplyTheme();
        SettingsVM.SyncThemeMode(ThemeMode);
        _ = _configManager.SetSettingAsync("theme_mode", ThemeMode);
        _logger.LogInformation("主题切换为: {Theme}", ThemeMode);
    }

    private void ApplyTheme()
    {
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = ThemeMode switch
            {
                "dark" => ThemeVariant.Dark,
                "light" => ThemeVariant.Light,
                _ => ThemeVariant.Default
            };
        }
    }

    private static string NormalizeThemeMode(string? themeMode) => themeMode switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => "system"
    };
}
