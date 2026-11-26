using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using LuckyLilliaDesktop.Utils.WebView2;
using Serilog;

namespace LuckyLilliaDesktop.Controls;

/// <summary>导航前预置到 <see cref="Url"/> 上的 cookie (host-only, path=/).</summary>
public sealed record WebViewCookie(string Url, string Name, string Value, TimeSpan Lifetime);

public sealed class WebViewNavigationCompletedEventArgs(bool isSuccess, int webErrorStatus) : EventArgs
{
    public bool IsSuccess { get; } = isSuccess;
    /// <summary>COREWEBVIEW2_WEB_ERROR_STATUS 枚举值, 只用于日志 / 提示.</summary>
    public int WebErrorStatus { get; } = webErrorStatus;
}

/// <summary>
/// 把 WebView2 挂进 Avalonia 的 NativeControlHost: 自建一个子 HWND 交给 Avalonia 定位, WebView2 控制器再挂在这个 HWND 上.
/// 非 Windows 平台不会创建任何原生内容 (调用方按 <see cref="WebView2Runtime.IsAvailable"/> 决定是否显示本控件).
/// 原生 HWND 永远盖在 Avalonia 内容之上 (airspace), 所以要显示遮罩时必须把本控件 IsVisible 设为 false.
/// 渲染路径上的坑 (分层宿主窗口 / 合成模式) 见 doc/webview2.md.
/// </summary>
public sealed partial class WebView2Host : NativeControlHost
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<WebView2Host, string?>(nameof(Source));

    /// <summary>
    /// true = 像关浏览器标签一样把 WebView 真正关掉: 控制器 Close + 放掉 environment, 浏览器进程 (连同渲染 / GPU 进程) 随后自己退出;
    /// 改回 false 时等旧浏览器进程退干净, 再新建 environment 和控制器并导航到 <see cref="Source"/>.
    /// </summary>
    public static readonly StyledProperty<bool> IsClosedProperty =
        AvaloniaProperty.Register<WebView2Host, bool>(nameof(IsClosed));

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_SETFOCUS = 0x0007;
    private const string WindowClassName = "LuckyLilliaWebView2Host";

    // WndProc 是静态的 UnmanagedCallersOnly 函数, 靠 HWND 找回实例 (只在 UI 线程访问)
    private static readonly Dictionary<nint, WebView2Host> Hosts = new();
    private static bool _classRegistered;
    private static nint _classNamePtr;

    private nint _hwnd;
    private int _generation;
    private ICoreWebView2Controller? _controller;
    private ICoreWebView2? _core;
    private NavigationCompletedHandler? _navigationHandler;
    private bool _backgroundMode;
    private readonly TrySuspendCompletedHandler _suspendHandler = new((hr, ok) =>
    {
        if (hr < 0 || !ok) Log.Information("WebView2 挂起未生效 (hr=0x{Hr:X8}, ok={Ok})", hr, ok);
        else Log.Information("WebView2 已挂起");
    });
    private readonly ScriptAddedHandler _scriptAddedHandler = new((hr, _) =>
    {
        if (hr < 0) Log.Warning("WebView2 AddScriptToExecuteOnDocumentCreated 失败: 0x{Hr:X8}", hr);
    });
    private readonly DevToolsMethodCompletedHandler _devToolsHandler = new((hr, _) =>
    {
        if (hr < 0) Log.Warning("WebView2 Emulation.setEmulatedMedia 失败: 0x{Hr:X8}", hr);
    });
    private NewWindowRequestedHandler? _newWindowHandler;
    private long _navigationToken;
    private long _newWindowToken;
    private Window? _window;

    public event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<Exception>? InitializationFailed;

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsClosed
    {
        get => GetValue(IsClosedProperty);
        set => SetValue(IsClosedProperty, value);
    }

    public bool IsReady => _core != null;

    /// <summary>
    /// 每次导航前调用, 返回要先写进 cookie 库的 cookie (null = 不用). 用来让 WebUI 免登录: 端口一变 WebUI 的登录 cookie 就对不上了.
    /// 每次导航都重新取, 密码改了下次导航就跟上.
    /// </summary>
    public Func<string, WebViewCookie?>? CookieProvider { get; set; }

    // 导航请求的序号: 设 cookie 是异步的, 回调回来时 Source 可能又变了, 只有最新一次请求才真正导航
    private int _navigateSeq;

    // 关闭后还在退出中的浏览器进程: 重新打开前要等它退干净, 同一个用户数据目录不能和正在退出的浏览器进程抢
    private int? _exitingBrowserPid;

    public WebView2Host()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyBackgroundColor();
            ApplyColorScheme();
        };
    }

    public void Reload()
    {
        try { _core?.Reload(); }
        catch (COMException ex) { Log.Warning(ex, "WebView2 Reload 失败"); }
    }

    /// <summary>WebView2 浏览器主进程的 PID, 退出时用来等它自己退干净.</summary>
    public int? BrowserProcessId
    {
        get
        {
            if (_core == null) return null;
            try
            {
                _core.get_BrowserProcessId(out var pid);
                return pid == 0 ? null : (int)pid;
            }
            catch (COMException) { return null; }
        }
    }

    /// <summary>退出前正常关掉 WebView (控制器 Close + 放掉 COM 引用), 之后由 WebView2Runtime.Shutdown 放掉 environment.</summary>
    public void Shutdown() => CloseWebView();

    /// <summary>
    /// 窗口藏起来 (最小化 / 收进托盘) 时让 WebView 停止渲染并挂起页面 (脚本冻结, 内核释放内存), 显示时恢复.
    /// TrySuspend 要求先 IsVisible=false; 设回 true 会自动恢复, 这里仍显式 Resume 一下.
    /// </summary>
    public void SetBackgroundMode(bool hidden)
    {
        _backgroundMode = hidden;
        if (_controller == null || _core == null) return;
        try
        {
            if (hidden)
            {
                _controller.put_IsVisible(0);
                if (Environment.GetEnvironmentVariable("LL_WEBVIEW_NO_SUSPEND") != "1" && _core is ICoreWebView2_3 core3) core3.TrySuspend(_suspendHandler);
            }
            else
            {
                if (_core is ICoreWebView2_3 core3) core3.Resume();
                _controller.put_IsVisible(1);
            }
        }
        catch (COMException ex) { Log.Warning(ex, "WebView2 切换后台模式失败"); }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = e.Root as Window;
        if (_window != null) _window.PositionChanged += OnWindowPositionChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window != null) _window.PositionChanged -= OnWindowPositionChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) NavigateToSource();
        else if (change.Property == IsClosedProperty)
        {
            if (IsClosed) CloseBrowser();
            else _ = ReopenAsync();
        }
        else if (change.Property == BoundsProperty || change.Property == IsVisibleProperty) RefreshBoundsFromWindow();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);
        return CreateWindowsControl(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsWindows())
        {
            base.DestroyNativeControlCore(control);
            return;
        }
        DestroyWindowsControl();
    }

    [SupportedOSPlatform("windows")]
    private unsafe IPlatformHandle CreateWindowsControl(IPlatformHandle parent)
    {
        EnsureWindowClass();
        _hwnd = Win32.CreateWindowExW(0, _classNamePtr, 0,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent.Handle, 0, Win32.GetModuleHandleW(null), 0);
        if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Hosts[_hwnd] = this;
        UnlayerHolder(parent.Handle);
        if (!IsClosed) _ = InitializeAsync(_hwnd, ++_generation);
        return new PlatformHandle(_hwnd, "HWND");
    }

    // Avalonia 在 WinUI / DirectComposition 模式下把 NativeControlHost 的宿主窗口建成 WS_EX_LAYERED
    // (WindowImpl: new Win32NativeControlHost(this, !UseRedirectionBitmap)), 这样 GDI 画的原生控件才有自己的表面.
    // 但 WebView2 用 DirectComposition 输出 (Intermediate D3D Window), 在分层祖先窗口里不会被合成, 页面一片空白.
    // 这里把分层样式去掉: WebView2 不需要 GDI 表面, Avalonia 的合成视觉树在子窗口之下 (isTopmost=0), 去掉后就能显示.
    [SupportedOSPlatform("windows")]
    private static void UnlayerHolder(nint holder)
    {
        const int GWL_EXSTYLE = -20;
        const nint WS_EX_LAYERED = 0x00080000;
        const uint SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        if (holder == 0) return;
        var exStyle = Win32.GetWindowLongPtrW(holder, GWL_EXSTYLE);
        if ((exStyle & WS_EX_LAYERED) == 0) return;
        Win32.SetWindowLongPtrW(holder, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
        Win32.SetWindowPos(holder, 0, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Log.Debug("WebView2 host: 已去掉 Avalonia 宿主窗口 0x{Holder:X} 的 WS_EX_LAYERED", holder);
    }

    [SupportedOSPlatform("windows")]
    private void DestroyWindowsControl()
    {
        _generation++;
        CloseWebView();
        if (_hwnd != 0)
        {
            Hosts.Remove(_hwnd);
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void EnsureWindowClass()
    {
        if (_classRegistered) return;
        _classNamePtr = Marshal.StringToHGlobalUni(WindowClassName);
        var wc = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
            lpfnWndProc = &WndProc,
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = _classNamePtr,
        };
        if (Win32.RegisterClassExW(in wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (Hosts.TryGetValue(hwnd, out var host)) host.OnWindowMessage(msg, lParam);
        }
        catch
        {
            // 异常不能穿过 UnmanagedCallersOnly 边界
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnWindowMessage(uint msg, nint lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                SetBounds((int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));
                break;
            case WM_SETFOCUS:
                try { _controller?.MoveFocus(WebViewConstants.MoveFocusReasonProgrammatic); }
                catch (COMException) { }
                break;
        }
    }

    // Avalonia 这边尺寸 / 可见性变了也主动对齐一次: 宿主子窗口尺寸没变时收不到 WM_SIZE
    private void RefreshBoundsFromWindow()
    {
        if (_hwnd == 0 || _controller == null || !OperatingSystem.IsWindows()) return;
        if (!Win32.GetClientRect(_hwnd, out var rect)) return;
        SetBounds(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private void CloseBrowser()
    {
        if (!OperatingSystem.IsWindows()) return;
        _generation++; // 还在初始化的话, 让它拿到控制器后直接关掉
        var pid = BrowserProcessId;
        if (pid != null) _exitingBrowserPid = pid;
        CloseWebView();
        WebView2Runtime.Shutdown();
        Log.Information("WebView2 已关闭, 浏览器进程 {Pid} 随后自行退出", pid);
    }

    private async Task ReopenAsync()
    {
        if (_hwnd == 0 || _controller != null) return;
        var generation = ++_generation;
        if (_exitingBrowserPid is int pid)
        {
            await WebView2Runtime.WaitForBrowserExitAsync(pid);
            if (generation != _generation) return;
            _exitingBrowserPid = null;
        }
        Log.Information("WebView2 重新打开");
        await InitializeAsync(_hwnd, generation);
    }

    private async Task InitializeAsync(nint hwnd, int generation)
    {
        try
        {
            var environment = await WebView2Runtime.GetEnvironmentAsync();
            if (generation != _generation) return;

            var controller = await CreateControllerAsync(environment, hwnd);
            if (generation != _generation)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            controller.get_CoreWebView2(out var core);
            _core = core;

            controller.put_IsVisible(1);
            ApplyBackgroundColor();
            ApplyColorScheme();
            InstallThemeFollowScript(core);
            RefreshBoundsFromWindow();

            _navigationHandler = new NavigationCompletedHandler(OnNavigationCompleted);
            core.add_NavigationCompleted(_navigationHandler, out _navigationToken);
            _newWindowHandler = new NewWindowRequestedHandler(OnNewWindowRequested);
            core.add_NewWindowRequested(_newWindowHandler, out _newWindowToken);

            Log.Information("WebView2 控制器已就绪 (hwnd=0x{Hwnd:X})", hwnd);
            NavigateToSource();
            // 窗口一启动就藏起来 (minimize_to_tray_on_start) 时控制器是在后台建好的
            if (_backgroundMode) SetBackgroundMode(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 初始化失败");
            InitializationFailed?.Invoke(this, ex);
        }
    }

    private static Task<ICoreWebView2Controller> CreateControllerAsync(ICoreWebView2Environment environment, nint hwnd)
    {
        var tcs = new TaskCompletionSource<ICoreWebView2Controller>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControllerCompletedHandler((hr, controller) =>
        {
            if (hr < 0 || controller == null)
                tcs.TrySetException(new COMException("CreateCoreWebView2Controller callback failed", hr == 0 ? unchecked((int)0x80004005) : hr));
            else
                tcs.TrySetResult(controller);
        });
        environment.CreateCoreWebView2Controller(hwnd, handler);
        return tcs.Task;
    }

    private void NavigateToSource()
    {
        var source = Source;
        var core = _core;
        if (core == null || string.IsNullOrWhiteSpace(source)) return;
        var seq = ++_navigateSeq;

        WebViewCookie? cookie = null;
        try { cookie = CookieProvider?.Invoke(source); }
        catch (Exception ex) { Log.Warning(ex, "WebView2 取导航前 cookie 失败"); }
        if (cookie == null)
        {
            Navigate(core, source);
            return;
        }

        // CDP Network.setCookie 不需要先 Network.enable; url 决定 host-only 的域和 path. 和 WebUI 前端 setCookie 的写法一致
        // (path=/, 不设 SameSite / HttpOnly), 同名同域同路径会覆盖前端自己存的那条. cookie 值不进日志.
        var parameters = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = cookie.Name,
            ["value"] = cookie.Value,
            ["url"] = cookie.Url,
            ["path"] = "/",
            ["expires"] = DateTimeOffset.UtcNow.Add(cookie.Lifetime).ToUnixTimeSeconds(),
        }.ToJsonString();
        // 回调对象交给原生侧后由 COM 引用计数保活 (见 WebView2Interop.cs 的 CCW 说明)
        var handler = new DevToolsMethodCompletedHandler((hr, _) =>
        {
            if (hr < 0) Log.Warning("WebView2 预置 cookie {Name} 失败: 0x{Hr:X8}", cookie.Name, hr);
            else Log.Information("WebView2 已为 {Url} 预置 cookie {Name}", cookie.Url, cookie.Name);
            // 这期间又导航了 / WebView 关掉重开了: 交给新的那次
            if (seq != _navigateSeq || !ReferenceEquals(core, _core)) return;
            Navigate(core, source);
        });
        try { core.CallDevToolsProtocolMethod("Network.setCookie", parameters, handler); }
        catch (COMException ex)
        {
            Log.Warning(ex, "WebView2 预置 cookie {Name} 失败", cookie.Name);
            Navigate(core, source);
        }
    }

    private void Navigate(ICoreWebView2 core, string source)
    {
        Log.Information("WebView2 导航: {Url}", source);
        try { core.Navigate(source); }
        catch (COMException ex)
        {
            Log.Error(ex, "WebView2 导航失败: {Url}", source);
            InitializationFailed?.Invoke(this, ex);
        }
    }

    private void SetBounds(int width, int height)
    {
        if (_controller == null) return;
        Log.Debug("WebView2 bounds {Width}x{Height}", width, height);
        try { _controller.put_Bounds(new RECT { Right = Math.Max(1, width), Bottom = Math.Max(1, height) }); }
        catch (COMException ex) { Log.Warning(ex, "WebView2 put_Bounds 失败"); }
    }

    // 让页面看到的 prefers-color-scheme 跟着桌面端主题走, WebUI 主题设成 "跟随系统" (默认) 的实例就和桌面端一起切;
    // 在 WebUI 里手动选了亮 / 暗的实例不受影响. 用 CDP 的 Emulation.setEmulatedMedia 是因为它在 ICoreWebView2 基接口上就有,
    // 正规的 ICoreWebView2Profile.PreferredColorScheme 要先把 ICoreWebView2_2 ~ _13 整条接口链声明出来 (见 doc/webview2.md).
    private void ApplyColorScheme()
    {
        if (_core == null) return;
        var scheme = ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";
        var parameters = $$"""{"features":[{"name":"prefers-color-scheme","value":"{{scheme}}"}]}""";
        try { _core.CallDevToolsProtocolMethod("Emulation.setEmulatedMedia", parameters, _devToolsHandler); }
        catch (COMException ex) { Log.Warning(ex, "WebView2 设置 prefers-color-scheme 失败"); }
    }

    // WebUI 把自己的主题档位存在 localStorage "llbot-theme" (zustand persist, 只存 mode; main.tsx 首屏也读它), 用户在 WebUI 里
    // 手动选过亮 / 暗的实例就不再看 prefers-color-scheme. 嵌在桌面端里时主题以桌面端为准: 每个文档的脚本跑起来之前先把 mode 改回
    // auto, 它就跟着上面模拟的 prefers-color-scheme 走. 键名和结构对应 LLBot FE 的 stores/themeStore.ts.
    private const string ThemeFollowScript = """
        (() => {
          try {
            const key = 'llbot-theme';
            let obj = null;
            try { obj = JSON.parse(localStorage.getItem(key) || 'null'); } catch {}
            if (!obj || typeof obj !== 'object') obj = { state: {}, version: 0 };
            obj.state = Object.assign({}, obj.state, { mode: 'auto' });
            localStorage.setItem(key, JSON.stringify(obj));
          } catch {}
        })();
        """;

    private void InstallThemeFollowScript(ICoreWebView2 core)
    {
        try { core.AddScriptToExecuteOnDocumentCreated(ThemeFollowScript, _scriptAddedHandler); }
        catch (COMException ex) { Log.Warning(ex, "WebView2 注入主题脚本失败"); }
    }

    // 页面自己的背景加载出来之前显示主题底色, 避免深色主题下闪白
    private void ApplyBackgroundColor()
    {
        if (_controller is not ICoreWebView2Controller2 controller2) return;
        if (!this.TryFindResource("MainBackground", ActualThemeVariant, out var resource) || resource is not ISolidColorBrush brush) return;
        var color = brush.Color;
        try { controller2.put_DefaultBackgroundColor(new WebViewColor { A = 255, R = color.R, G = color.G, B = color.B }); }
        catch (COMException) { }
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        try { _controller?.NotifyParentWindowPositionChanged(); }
        catch (COMException) { }
    }

    private void OnNavigationCompleted(ICoreWebView2NavigationCompletedEventArgs args)
    {
        args.get_IsSuccess(out var isSuccess);
        args.get_WebErrorStatus(out var status);
        Log.Information("WebView2 导航完成: success={Success}, status={Status}", isSuccess != 0, status);
        // 模拟的媒体特性跨导航会保留, 这里再钉一次只是兜底
        ApplyColorScheme();
        NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs(isSuccess != 0, status));
    }

    // WebUI 里的外链 (文档 / GitHub) 交给系统浏览器, 不在嵌入视图里开新窗口
    private static void OnNewWindowRequested(ICoreWebView2NewWindowRequestedEventArgs args)
    {
        args.get_Uri(out var uri);
        args.put_Handled(1);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true }); }
        catch { }
    }

    private void CloseWebView()
    {
        var core = _core;
        var controller = _controller;
        _core = null;
        _controller = null;
        try
        {
            if (core != null)
            {
                if (_navigationHandler != null) core.remove_NavigationCompleted(_navigationToken);
                if (_newWindowHandler != null) core.remove_NewWindowRequested(_newWindowToken);
            }
            controller?.Close();
        }
        catch (COMException) { }
        _navigationHandler = null;
        _newWindowHandler = null;
        // 显式放掉 RCW 引用, 不等 GC: 父 HWND 马上就要销毁. ComObject 是 sealed 且只靠 IDynamicInterfaceCastable
        // 暴露接口, 所以要先转成 object 再判断.
        ((object?)core as ComObject)?.FinalRelease();
        ((object?)controller as ComObject)?.FinalRelease();
    }

    private static partial class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public nint lpszMenuName;
            public nint lpszClassName;
            public nint hIconSm;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint CreateWindowExW(uint dwExStyle, nint lpClassName, nint lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(nint hWnd);

        [LibraryImport("user32.dll")]
        public static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

        [LibraryImport("user32.dll")]
        public static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

        [LibraryImport("user32.dll")]
        public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint GetModuleHandleW(string? lpModuleName);
    }
}
