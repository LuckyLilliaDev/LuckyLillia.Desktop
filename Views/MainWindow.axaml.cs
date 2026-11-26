using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.VisualTree;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using LuckyLilliaDesktop.Utils.WebView2;
using LuckyLilliaDesktop.ViewModels;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Views;

public partial class MainWindow : Window
{
    private IConfigManager? _configManager;
    private ILogger<MainWindow>? _logger;
    private bool _windowPositionLoaded;
    private bool _minimizeToTrayOnStartChecked;
    private Win32Properties.CustomWndProcHookCallback? _snapLayoutHook;
    private MainWindowViewModel? _viewModel;
    private bool _isBackgroundMode;

    public MainWindow()
    {
        InitializeComponent();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            SystemDecorations = SystemDecorations.None;
            AddResizeGripsForLinux();
        }

        Closing += OnWindowClosing;
        PositionChanged += OnPositionChanged;
        SizeChanged += OnSizeChanged;

        // 监听窗口可见性和窗口状态变化以优化性能/更新最大化按钮图标
        PropertyChanged += OnWindowPropertyChanged;
        UpdateMaximizeRestoreIcon();
        UpdateWindowFrameMargin();
        EnableWindowsSnapLayout();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // 标题栏里的页面导航自己处理点击, 不当成拖动
        if (e.Source is Visual source && source.FindAncestorOfType<ListBox>(true) != null) return;

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeRestoreIcon();
    }

    private void UpdateMaximizeRestoreIcon()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIconPath.IsVisible = !isMaximized;
        RestoreIconPath.IsVisible = isMaximized;
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(IsVisible))
        {
            HandleVisibilityChanged((bool?)e.NewValue ?? false);
        }
        else if (e.Property.Name == nameof(WindowState))
        {
            UpdateMaximizeRestoreIcon();
            UpdateWindowFrameMargin();
            SetBackgroundMode(WindowState == WindowState.Minimized || !IsVisible);
        }
    }

    private void UpdateWindowFrameMargin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // 扩展客户区后，最大化时保留系统不可见边框，避免内容被屏幕边缘裁掉。
        Margin = new Thickness(WindowState == WindowState.Maximized ? 7 : 0);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static bool IsLeftMouseButtonDown()
    {
        const int VK_LBUTTON = 1;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    }

    private void EnableWindowsSnapLayout()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        const int HTCLIENT = 1;
        const int HTMAXBUTTON = 9;
        const uint WM_NCHITTEST = 0x0084;

        var pointerOnButton = false;

        nint ProcHookCallback(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
        {
            if (!MaximizeButton.IsVisible || msg != WM_NCHITTEST) return 0;

            var point = new PixelPoint((short)(ToInt32(lParam) & 0xffff), (short)(ToInt32(lParam) >> 16));
            var buttonSize = MaximizeButton.DesiredSize;
            var buttonLeftTop = MaximizeButton.PointToScreen(FlowDirection == Avalonia.Media.FlowDirection.LeftToRight
                ? new Point(buttonSize.Width, 0)
                : new Point(0, 0));

            var x = (buttonLeftTop.X - point.X) / RenderScaling;
            var y = (point.Y - buttonLeftTop.Y) / RenderScaling;

            if (new Rect(default, buttonSize).Contains(new Point(x, y)))
            {
                handled = true;

                if (!pointerOnButton)
                {
                    pointerOnButton = true;
                    MaximizeButton.Classes.Add("snap-hover");
                }

                return IsLeftMouseButtonDown() ? HTCLIENT : HTMAXBUTTON;
            }

            if (pointerOnButton)
            {
                pointerOnButton = false;
                MaximizeButton.Classes.Remove("snap-hover");
            }

            return 0;
        }

        static int ToInt32(IntPtr ptr) => IntPtr.Size == 4 ? ptr.ToInt32() : (int)(ptr.ToInt64() & 0xffffffff);

        _snapLayoutHook = new Win32Properties.CustomWndProcHookCallback(ProcHookCallback);
        Win32Properties.AddWndProcHookCallback(this, _snapLayoutHook);
    }

    private void AddResizeGripsForLinux()
    {
        var resizeBorders = new[]
        {
            new { Tag = "North", Horizontal = HorizontalAlignment.Stretch, Vertical = VerticalAlignment.Top, Width = double.NaN, Height = 6d, Cursor = StandardCursorType.SizeNorthSouth },
            new { Tag = "South", Horizontal = HorizontalAlignment.Stretch, Vertical = VerticalAlignment.Bottom, Width = double.NaN, Height = 6d, Cursor = StandardCursorType.SizeNorthSouth },
            new { Tag = "West", Horizontal = HorizontalAlignment.Left, Vertical = VerticalAlignment.Stretch, Width = 6d, Height = double.NaN, Cursor = StandardCursorType.SizeWestEast },
            new { Tag = "East", Horizontal = HorizontalAlignment.Right, Vertical = VerticalAlignment.Stretch, Width = 6d, Height = double.NaN, Cursor = StandardCursorType.SizeWestEast },
            new { Tag = "NorthWest", Horizontal = HorizontalAlignment.Left, Vertical = VerticalAlignment.Top, Width = 12d, Height = 12d, Cursor = StandardCursorType.TopLeftCorner },
            new { Tag = "NorthEast", Horizontal = HorizontalAlignment.Right, Vertical = VerticalAlignment.Top, Width = 12d, Height = 12d, Cursor = StandardCursorType.TopRightCorner },
            new { Tag = "SouthWest", Horizontal = HorizontalAlignment.Left, Vertical = VerticalAlignment.Bottom, Width = 12d, Height = 12d, Cursor = StandardCursorType.BottomLeftCorner },
            new { Tag = "SouthEast", Horizontal = HorizontalAlignment.Right, Vertical = VerticalAlignment.Bottom, Width = 12d, Height = 12d, Cursor = StandardCursorType.BottomRightCorner },
        };

        foreach (var borderInfo in resizeBorders)
        {
            var border = new Border
            {
                Tag = borderInfo.Tag,
                Width = borderInfo.Width,
                Height = borderInfo.Height,
                Background = Avalonia.Media.Brushes.Transparent,
                Cursor = new Cursor(borderInfo.Cursor),
                HorizontalAlignment = borderInfo.Horizontal,
                VerticalAlignment = borderInfo.Vertical,
                IsHitTestVisible = true,
            };

            border.PointerPressed += ResizeGrip_PointerPressed;
            RootPanel.Children.Add(border);
        }
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal) return;
        if (sender is not Border { Tag: string edge }) return;

        var windowEdge = edge switch
        {
            "North" => WindowEdge.North,
            "South" => WindowEdge.South,
            "West" => WindowEdge.West,
            "East" => WindowEdge.East,
            "NorthWest" => WindowEdge.NorthWest,
            "NorthEast" => WindowEdge.NorthEast,
            "SouthWest" => WindowEdge.SouthWest,
            "SouthEast" => WindowEdge.SouthEast,
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null)
        };

        BeginResizeDrag(windowEdge, e);
        e.Handled = true;
    }

    private void HandleVisibilityChanged(bool isVisible)
    {
        SetBackgroundMode(!isVisible || WindowState == WindowState.Minimized);
    }

    private void SetBackgroundMode(bool enabled)
    {
        if (_isBackgroundMode == enabled) return;
        _isBackgroundMode = enabled;

        // 窗口藏起来时日志列表不用再跟着刷, WebView 也停渲染并挂起 (见 doc/webview2.md "后台挂起")
        if (DataContext is MainWindowViewModel vm)
        {
            if (enabled) vm.HomeVM.TerminalLog.PauseUIUpdates();
            else vm.HomeVM.TerminalLog.ResumeUIUpdates();
        }
        HomePageView.SetWebViewBackgroundMode(enabled);

        if (enabled)
        {
            _ = TrimBackgroundMemoryAsync();
        }
    }

    private static async Task TrimBackgroundMemoryAsync()
    {
        try
        {
            await Task.Delay(250);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                EmptyWorkingSet(process.Handle);
            }
        }
        catch
        {
            // Best-effort memory trim when the UI is hidden/minimized.
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await LoadWindowPositionAsync();
        await CheckMinimizeToTrayOnStartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel = null;
        }

        if (_snapLayoutHook is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(this, _snapLayoutHook);
            _snapLayoutHook = null;
        }

        base.OnClosed(e);
    }

    private async Task CheckMinimizeToTrayOnStartAsync()
    {
        // 只在首次启动时检查，避免从托盘恢复时再次最小化
        if (_minimizeToTrayOnStartChecked || _configManager == null) return;
        _minimizeToTrayOnStartChecked = true;

        try
        {
            var config = await _configManager.LoadConfigAsync();
            if (config.MinimizeToTrayOnStart)
            {
                await Task.Delay(100);
                MinimizeToTray();
            }
        }
        catch { }
    }

    private async Task LoadWindowPositionAsync()
    {
        if (_windowPositionLoaded || _configManager == null) return;
        _windowPositionLoaded = true;

        try
        {
            var config = await _configManager.LoadConfigAsync();
            var isFirstLaunch = !config.WindowLeft.HasValue && !config.WindowTop.HasValue;

            if (isFirstLaunch)
            {
                _logger?.LogInformation("首次启动，计算初始窗口位置");
                // 首次启动，根据屏幕大小计算窗口尺寸和位置
                CalculateInitialWindowBounds();
            }
            else
            {
                // 恢复窗口大小（只恢复普通窗口尺寸，并限制在当前屏幕工作区内，避免上次最大化后像全屏一样打开）
                RestoreNormalWindowSize(config.WindowWidth, config.WindowHeight);

                // 只有位置有效时才恢复（避免负值过大的情况）
                if (config.WindowLeft.HasValue && config.WindowTop.HasValue
                    && config.WindowLeft.Value > -1000 && config.WindowTop.Value > -1000)
                {
                    Position = new PixelPoint((int)config.WindowLeft.Value, (int)config.WindowTop.Value);
                }

                _logger?.LogInformation("恢复窗口位置: ({X}, {Y}), 大小: {Width}x{Height}",
                    Position.X, Position.Y, Width, Height);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "加载窗口位置失败");
        }
    }

    private void RestoreNormalWindowSize(int savedWidth, int savedHeight)
    {
        var targetWidth = savedWidth > 0 ? savedWidth : 960;
        var targetHeight = savedHeight > 0 ? savedHeight : 640;

        var screen = Screens.ScreenFromWindow(this);
        if (screen != null)
        {
            var workingArea = screen.WorkingArea;
            var screenWidth = workingArea.Width / screen.Scaling;
            var screenHeight = workingArea.Height / screen.Scaling;

            targetWidth = (int)Math.Min(targetWidth, Math.Max(MinWidth, screenWidth * 0.9));
            targetHeight = (int)Math.Min(targetHeight, Math.Max(MinHeight, screenHeight * 0.9));
        }

        Width = Math.Max(MinWidth, targetWidth);
        Height = Math.Max(MinHeight, targetHeight);
        WindowState = WindowState.Normal;
    }

    private void CalculateInitialWindowBounds()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen == null) return;

        var workingArea = screen.WorkingArea;
        var screenWidth = workingArea.Width / screen.Scaling;
        var screenHeight = workingArea.Height / screen.Scaling;

        // 窗口占屏幕的 70%，但不超过 1200x800，不小于 900x600
        var targetWidth = Math.Max(900, Math.Min(1200, screenWidth * 0.7));
        var targetHeight = Math.Max(600, Math.Min(800, screenHeight * 0.7));

        Width = targetWidth;
        Height = targetHeight;

        // 居中显示，留出任务栏空间
        var left = workingArea.X / screen.Scaling + (screenWidth - targetWidth) / 2;
        var top = workingArea.Y / screen.Scaling + (screenHeight - targetHeight) / 2;

        Position = new PixelPoint((int)left, (int)top);
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        _logger?.LogDebug("窗口位置改变: ({X}, {Y})", e.Point.X, e.Point.Y);
        ScheduleSaveWindowPosition();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _logger?.LogDebug("窗口大小改变: {Width}x{Height}", e.NewSize.Width, e.NewSize.Height);
        ScheduleSaveWindowPosition();
    }

    private CancellationTokenSource? _savePositionCts;

    private void ScheduleSaveWindowPosition()
    {
        if (_configManager == null || !_windowPositionLoaded)
        {
            _logger?.LogDebug("跳过保存窗口位置: ConfigManager={ConfigManager}, Loaded={Loaded}",
                _configManager != null, _windowPositionLoaded);
            return;
        }

        // 只保存普通窗口位置和大小，避免最大化/最小化尺寸被保存后下次启动像全屏一样打开
        if (WindowState != WindowState.Normal)
        {
            _logger?.LogDebug("窗口不是普通状态，跳过保存位置: {WindowState}", WindowState);
            return;
        }

        // 检查位置是否有效（负值过大说明窗口在屏幕外）
        if (Position.X < -1000 || Position.Y < -1000)
        {
            _logger?.LogDebug("窗口位置无效，跳过保存: ({X}, {Y})", Position.X, Position.Y);
            return;
        }

        _savePositionCts?.Cancel();
        _savePositionCts = new CancellationTokenSource();
        var token = _savePositionCts.Token;

        // 在 UI 线程上获取窗口属性
        var left = Position.X;
        var top = Position.Y;
        var width = Width;
        var height = Height;

        _logger?.LogDebug("计划保存窗口位置 (2秒后)");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, token);
                if (token.IsCancellationRequested) return;

                await _configManager.SetSettingAsync("window_left", (int)left);
                await _configManager.SetSettingAsync("window_top", (int)top);
                await _configManager.SetSettingAsync("window_width", (int)width);
                await _configManager.SetSettingAsync("window_height", (int)height);

                _logger?.LogInformation("窗口位置已保存: ({X}, {Y}), 大小: {Width}x{Height}",
                    left, top, width, height);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("保存窗口位置已取消");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存窗口位置失败");
            }
        }, token);
    }

    private async Task SaveWindowPositionImmediatelyAsync()
    {
        if (_configManager == null || !_windowPositionLoaded) return;

        _savePositionCts?.Cancel();

        if (WindowState != WindowState.Normal) return;
        if (Position.X < -1000 || Position.Y < -1000) return;

        try
        {
            await _configManager.SetSettingAsync("window_left", (int)Position.X);
            await _configManager.SetSettingAsync("window_top", (int)Position.Y);
            await _configManager.SetSettingAsync("window_width", (int)Width);
            await _configManager.SetSettingAsync("window_height", (int)Height);

            _logger?.LogInformation("窗口位置立即保存: ({X}, {Y}), 大小: {Width}x{Height}",
                Position.X, Position.Y, Width, Height);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "立即保存窗口位置失败");
        }
    }

    /// <summary>
    /// 公开的保存窗口状态方法，供 App 调用
    /// </summary>
    public async Task SaveWindowStateAsync()
    {
        await SaveWindowPositionImmediatelyAsync();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _viewModel = vm;
            HomePageView.DataContext = vm.HomeVM;
            SettingsPageView.DataContext = vm.SettingsVM;
            AboutPageView.DataContext = vm.AboutVM;
            vm.PropertyChanged += ViewModel_PropertyChanged;
            SetVisiblePage(vm.SelectedIndex);

            var app = Application.Current as App;
            _configManager = app?.Services?.GetService(typeof(IConfigManager)) as IConfigManager;
            _logger = app?.Services?.GetService(typeof(ILogger<MainWindow>)) as ILogger<MainWindow>;

            vm.HomeVM.ConfirmDialog = ShowConfirmDialogAsync;
            vm.HomeVM.ShowAlertDialog = ShowAlertDialogAsync;
            vm.HomeVM.ShowLoadingDialog = ShowLoadingDialogAsync;
            vm.SettingsVM.ShowAlertDialog = ShowAlertDialogAsync;
            vm.AboutVM.ConfirmDialog = ShowConfirmDialogAsync;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedIndex) || sender is not MainWindowViewModel vm) return;
        SetVisiblePage(vm.SelectedIndex);
    }

    // 控制面板里是原生 WebView2 窗口, 盖在 Avalonia 内容上且不参与透明度动画, 所以页面切换不做淡入淡出
    private void SetVisiblePage(int selectedIndex)
    {
        HomePageView.IsVisible = selectedIndex == 0;
        SettingsPageView.IsVisible = selectedIndex == 1;
        AboutPageView.IsVisible = selectedIndex == 2;
    }

    // Avalonia 拒绝给 non-visible owner 挂模态子窗口 ("Cannot show window with non-visible owner").
    // 启动即最小化到托盘时主窗口已 Hide(), 但启动流程仍可能弹系统时间提示等对话框, 先把主窗口恢复出来.
    private void EnsureVisibleForDialog()
    {
        if (IsVisible && WindowState != WindowState.Minimized) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        SetBackgroundMode(false);
    }

    private async Task ShowAlertDialogAsync(string title, string message)
    {
        EnsureVisibleForDialog();
        var dialog = new AlertDialog(message);
        await dialog.ShowDialog<object?>(this);
    }

    private async Task ShowLoadingDialogAsync(string message, Func<Task> action)
    {
        EnsureVisibleForDialog();
        var dialog = new LoadingDialog(message);
        var dialogTask = dialog.ShowDialog<object?>(this);

        try
        {
            await action();
        }
        finally
        {
            dialog.Close();
        }

        await dialogTask;
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        EnsureVisibleForDialog();
        var dialog = new ConfirmDialog(message);
        var result = await dialog.ShowDialog<bool?>(this);
        return result == true;
    }

    private bool _isClosingHandled;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosingHandled) return;

        var closeToTray = _configManager?.GetSetting<bool?>("close_to_tray", null);

        if (closeToTray == true)
        {
            e.Cancel = true;
            MinimizeToTray();
        }
        else if (closeToTray == false)
        {
            _isClosingHandled = true;
            e.Cancel = true;
            await ExitAsync();
        }
        else
        {
            e.Cancel = true;
            await ShowCloseDialogAsync();
        }
    }

    private async Task ExitAsync()
    {
        if (Application.Current is App app)
        {
            await app.ExitApplicationAsync();
        }
    }

    private async Task ShowCloseDialogAsync()
    {
        var dialog = new CloseDialog();
        var result = await dialog.ShowDialog<CloseDialogResult?>(this);

        if (result == null) return;

        if (result.RememberChoice && _configManager != null)
        {
            await _configManager.SetSettingAsync("close_to_tray", result.MinimizeToTray);
        }

        if (result.MinimizeToTray)
        {
            MinimizeToTray();
        }
        else
        {
            _isClosingHandled = true;
            await ExitAsync();
        }
    }

    private void MinimizeToTray()
    {
        Hide();
        SetBackgroundMode(true);
    }

    // 本进程在 KILL_ON_JOB_CLOSE 的 Job Object 里, 进程一退整棵树 (含 WebView2 的几个进程) 会被直接杀掉, 浏览器进程来不及把
    // 还没落盘的 cookie 写下去 (Chromium 每 30 秒才提交一次; WebUI 的登录态就是个 cookie). 所以退出前先正常关掉 WebView、放掉
    // environment, 等浏览器进程自己退出 (通常几百毫秒), 最多等 3 秒.
    public async Task ShutdownWebViewAsync()
    {
        var pid = HomePageView.WebView.BrowserProcessId;
        HomePageView.WebView.Shutdown();
        WebView2Runtime.Shutdown();
        if (pid is not int browserPid) return;
        // 超时就交给 Job Object 收尾
        await WebView2Runtime.WaitForBrowserExitAsync(browserPid);
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        SetBackgroundMode(false);
    }
}

/// <summary>
/// 关闭对话框结果
/// </summary>
public class CloseDialogResult
{
    public bool MinimizeToTray { get; set; }
    public bool RememberChoice { get; set; }
}
