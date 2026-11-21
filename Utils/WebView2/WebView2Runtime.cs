using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;

namespace LuckyLilliaDesktop.Utils.WebView2;

/// <summary>
/// WebView2 运行时探测 + 进程内唯一的 CoreWebView2Environment.
/// Environment 只依赖用户数据目录, 不依赖窗口, 所以可以在控件创建前预热 (浏览器进程启动要几百毫秒).
/// 所有成员只能在 UI 线程调用 (WebView2 的回调投递回创建线程的消息循环).
/// </summary>
internal static class WebView2Runtime
{
    /// <summary>Evergreen 运行时引导安装包 (微软官方固定链接).</summary>
    public const string InstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static string? _version;
    private static bool _probed;
    private static Task<ICoreWebView2Environment>? _environment;

    /// <summary>运行时版本号; 非 Windows / 未安装运行时 / 加载器缺失时为 null.</summary>
    public static string? AvailableVersion
    {
        get
        {
            if (_probed) return _version;
            _probed = true;
            _version = Probe();
            return _version;
        }
    }

    public static bool IsAvailable => AvailableVersion != null;

    /// <summary>用户数据 (cookie / 缓存) 跟随程序目录, 便携部署时一起带走.</summary>
    public static string UserDataFolder => Path.GetFullPath(Path.Combine("data", "webview2"));

    private static string? Probe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hr = WebView2Loader.GetAvailableCoreWebView2BrowserVersionString(null, out var versionPtr);
            if (hr < 0 || versionPtr == 0) return null;
            try { return Marshal.PtrToStringUni(versionPtr); }
            finally { Marshal.FreeCoTaskMem(versionPtr); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Debug 下 WebView2Loader.dll 没跟着拷到输出目录等情况: 当作没有运行时, 走浏览器打开的回退
            return null;
        }
    }

    /// <summary>
    /// 放掉缓存的 environment, 浏览器进程没有客户端后会自己正常退出 (把 cookie 等落盘).
    /// 之后再调 <see cref="GetEnvironmentAsync"/> 会新建一个; 新建前先用 <see cref="WaitForBrowserExitAsync"/> 等旧浏览器进程退干净.
    /// </summary>
    public static void Shutdown()
    {
        var task = _environment;
        _environment = null;
        if (task is { IsCompletedSuccessfully: true }) ((object)task.Result as ComObject)?.FinalRelease();
    }

    /// <summary>等浏览器主进程自己退出, 超时返回 false.</summary>
    public static async Task<bool> WaitForBrowserExitAsync(int pid, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exited = false;
        while (!exited && sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                exited = process.HasExited;
            }
            catch (ArgumentException) { exited = true; }
            catch (InvalidOperationException) { exited = true; }
            if (!exited) await Task.Delay(50);
        }
        if (exited) Log.Information("WebView2 浏览器进程已退出 ({Ms} ms)", sw.ElapsedMilliseconds);
        else Log.Warning("WebView2 浏览器进程 {Pid} {Timeout} ms 内没退出", pid, timeoutMs);
        return exited;
    }

    public static Task<ICoreWebView2Environment> GetEnvironmentAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_environment is { IsFaulted: true }) _environment = null;
        return _environment ??= CreateEnvironmentAsync();
    }

    private static unsafe Task<ICoreWebView2Environment> CreateEnvironmentAsync()
    {
        if (!IsAvailable) return Task.FromException<ICoreWebView2Environment>(new PlatformNotSupportedException("WebView2 runtime is not available"));

        var tcs = new TaskCompletionSource<ICoreWebView2Environment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new EnvironmentCompletedHandler((hr, environment) =>
        {
            if (hr < 0 || environment == null)
            {
                Log.Warning("WebView2 environment 创建失败: hr=0x{Hr:X8}", hr);
                tcs.TrySetException(new COMException("CreateCoreWebView2EnvironmentWithOptions callback failed", hr == 0 ? unchecked((int)0x80004005) : hr));
            }
            else
            {
                Log.Information("WebView2 environment 已创建 (运行时 {Version}, 用户数据 {Folder})", AvailableVersion, UserDataFolder);
                tcs.TrySetResult(environment);
            }
        });

        Directory.CreateDirectory(UserDataFolder);
        var handlerPtr = ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.ConvertToUnmanaged(handler);
        try
        {
            var hr = WebView2Loader.CreateCoreWebView2EnvironmentWithOptions(null, UserDataFolder, 0, (nint)handlerPtr);
            if (hr < 0)
            {
                Log.Warning("CreateCoreWebView2EnvironmentWithOptions 返回 0x{Hr:X8}", hr);
                tcs.TrySetException(new COMException("CreateCoreWebView2EnvironmentWithOptions failed", hr));
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.Free(handlerPtr);
        }
        return tcs.Task;
    }
}
