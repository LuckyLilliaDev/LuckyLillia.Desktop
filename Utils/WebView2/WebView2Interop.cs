using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace LuckyLilliaDesktop.Utils.WebView2;

// 手写的最小 WebView2 COM 声明 (源生成 COM 互操作, NativeAOT 可用).
// 方法顺序 = WebView2 SDK 1.0.4314.35 的 WebView2.h vtable 序, 增删会错位; 用不到的槽位参数只用 nint 占位.
// 原生对象返回的 LPWSTR 由 CoTaskMemAlloc 分配, out string 的生成代码会用 CoTaskMemFree 释放, 不用自己管.
// BOOL 一律声明为 int. 见 doc/webview2.md.

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

// 字段顺序照 COREWEBVIEW2_COLOR (A, R, G, B)
[StructLayout(LayoutKind.Sequential)]
internal struct WebViewColor
{
    public byte A;
    public byte R;
    public byte G;
    public byte B;
}

internal static class WebViewConstants
{
    public const int MoveFocusReasonProgrammatic = 0;
}

[GeneratedComInterface]
[Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d")]
internal partial interface ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    void Invoke(int errorCode, ICoreWebView2Environment? createdEnvironment);
}

[GeneratedComInterface]
[Guid("b96d755e-0319-4e92-a296-23436f46a1fc")]
internal partial interface ICoreWebView2Environment
{
    void CreateCoreWebView2Controller(nint parentWindow, ICoreWebView2CreateCoreWebView2ControllerCompletedHandler handler);
    void CreateWebResourceResponse(nint content, int statusCode, nint reasonPhrase, nint headers, out nint response);
    void get_BrowserVersionString(out nint versionInfo);
    void add_NewBrowserVersionAvailable(nint eventHandler, out long token);
    void remove_NewBrowserVersionAvailable(long token);
}

[GeneratedComInterface]
[Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c")]
internal partial interface ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
    void Invoke(int errorCode, ICoreWebView2Controller? createdController);
}

[GeneratedComInterface]
[Guid("4d00c0d1-9434-4eb6-8078-8697a560334f")]
internal partial interface ICoreWebView2Controller
{
    void get_IsVisible(out int isVisible);
    void put_IsVisible(int isVisible);
    void get_Bounds(out RECT bounds);
    void put_Bounds(RECT bounds);
    void get_ZoomFactor(out double zoomFactor);
    void put_ZoomFactor(double zoomFactor);
    void add_ZoomFactorChanged(nint eventHandler, out long token);
    void remove_ZoomFactorChanged(long token);
    void SetBoundsAndZoomFactor(RECT bounds, double zoomFactor);
    void MoveFocus(int reason);
    void add_MoveFocusRequested(nint eventHandler, out long token);
    void remove_MoveFocusRequested(long token);
    void add_GotFocus(nint eventHandler, out long token);
    void remove_GotFocus(long token);
    void add_LostFocus(nint eventHandler, out long token);
    void remove_LostFocus(long token);
    void add_AcceleratorKeyPressed(nint eventHandler, out long token);
    void remove_AcceleratorKeyPressed(long token);
    void get_ParentWindow(out nint parentWindow);
    void put_ParentWindow(nint parentWindow);
    void NotifyParentWindowPositionChanged();
    void Close();
    void get_CoreWebView2(out ICoreWebView2 coreWebView2);
}

[GeneratedComInterface]
[Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab")]
internal partial interface ICoreWebView2Controller2 : ICoreWebView2Controller
{
    void get_DefaultBackgroundColor(out WebViewColor value);
    void put_DefaultBackgroundColor(WebViewColor value);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("76eceacb-0462-4d94-ac83-423a6793775e")]
internal partial interface ICoreWebView2
{
    void get_Settings(out nint settings);
    void get_Source(out string uri);
    void Navigate(string uri);
    void NavigateToString(string htmlContent);
    void add_NavigationStarting(nint eventHandler, out long token);
    void remove_NavigationStarting(long token);
    void add_ContentLoading(nint eventHandler, out long token);
    void remove_ContentLoading(long token);
    void add_SourceChanged(nint eventHandler, out long token);
    void remove_SourceChanged(long token);
    void add_HistoryChanged(nint eventHandler, out long token);
    void remove_HistoryChanged(long token);
    void add_NavigationCompleted(ICoreWebView2NavigationCompletedEventHandler eventHandler, out long token);
    void remove_NavigationCompleted(long token);
    void add_FrameNavigationStarting(nint eventHandler, out long token);
    void remove_FrameNavigationStarting(long token);
    void add_FrameNavigationCompleted(nint eventHandler, out long token);
    void remove_FrameNavigationCompleted(long token);
    void add_ScriptDialogOpening(nint eventHandler, out long token);
    void remove_ScriptDialogOpening(long token);
    void add_PermissionRequested(nint eventHandler, out long token);
    void remove_PermissionRequested(long token);
    void add_ProcessFailed(nint eventHandler, out long token);
    void remove_ProcessFailed(long token);
    void AddScriptToExecuteOnDocumentCreated(string javaScript, ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler? handler);
    void RemoveScriptToExecuteOnDocumentCreated(string id);
    void ExecuteScript(string javaScript, nint handler);
    void CapturePreview(int imageFormat, nint imageStream, nint handler);
    void Reload();
    void PostWebMessageAsJson(string webMessageAsJson);
    void PostWebMessageAsString(string webMessageAsString);
    void add_WebMessageReceived(nint handler, out long token);
    void remove_WebMessageReceived(long token);
    void CallDevToolsProtocolMethod(string methodName, string parametersAsJson, ICoreWebView2CallDevToolsProtocolMethodCompletedHandler? handler);
    void get_BrowserProcessId(out uint value);
    void get_CanGoBack(out int canGoBack);
    void get_CanGoForward(out int canGoForward);
    void GoBack();
    void GoForward();
    void GetDevToolsProtocolEventReceiver(string eventName, out nint receiver);
    void Stop();
    void add_NewWindowRequested(ICoreWebView2NewWindowRequestedEventHandler eventHandler, out long token);
    void remove_NewWindowRequested(long token);
    void add_DocumentTitleChanged(nint eventHandler, out long token);
    void remove_DocumentTitleChanged(long token);
    void get_DocumentTitle(out string title);
    void AddHostObjectToScript(string name, nint variant);
    void RemoveHostObjectFromScript(string name);
    void OpenDevToolsWindow();
    void add_ContainsFullScreenElementChanged(nint eventHandler, out long token);
    void remove_ContainsFullScreenElementChanged(long token);
    void get_ContainsFullScreenElement(out int containsFullScreenElement);
    void add_WebResourceRequested(nint eventHandler, out long token);
    void remove_WebResourceRequested(long token);
    void AddWebResourceRequestedFilter(string uri, int resourceContext);
    void RemoveWebResourceRequestedFilter(string uri, int resourceContext);
    void add_WindowCloseRequested(nint eventHandler, out long token);
    void remove_WindowCloseRequested(long token);
}

// ICoreWebView2_2 / _3: 只用 TrySuspend / Resume, 其余方法按 WebView2.h 顺序占位, 不要调
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("9E8F0CF8-E670-4B5E-B2BC-73E061E3184C")]
internal partial interface ICoreWebView2_2 : ICoreWebView2
{
    void add_WebResourceResponseReceived(nint eventHandler, out long token);
    void remove_WebResourceResponseReceived(long token);
    void NavigateWithWebResourceRequest(nint request);
    void add_DOMContentLoaded(nint eventHandler, out long token);
    void remove_DOMContentLoaded(long token);
    void get_CookieManager(out nint cookieManager);
    void get_Environment(out nint environment);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A0D6DF20-3B92-416D-AA0C-437A9C727857")]
internal partial interface ICoreWebView2_3 : ICoreWebView2_2
{
    void TrySuspend(ICoreWebView2TrySuspendCompletedHandler? handler);
    void Resume();
    void get_IsSuspended(out int isSuspended);
    void SetVirtualHostNameToFolderMapping(string hostName, string folderPath, int accessKind);
    void ClearVirtualHostNameToFolderMapping(string hostName);
}

[GeneratedComInterface]
[Guid("00f206a7-9d17-4605-91f6-4e8e4de192e3")]
internal partial interface ICoreWebView2TrySuspendCompletedHandler
{
    void Invoke(int errorCode, int isSuccessful);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("b99369f3-9b11-47b5-bc6f-8e7895fcea17")]
internal partial interface ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    void Invoke(int errorCode, string? id);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")]
internal partial interface ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    void Invoke(int errorCode, string? returnObjectAsJson);
}

[GeneratedComInterface]
[Guid("d33a35bf-1c49-4f98-93ab-006e0533fe1c")]
internal partial interface ICoreWebView2NavigationCompletedEventHandler
{
    void Invoke(ICoreWebView2? sender, ICoreWebView2NavigationCompletedEventArgs? args);
}

[GeneratedComInterface]
[Guid("30d68b7d-20d9-4752-a9ca-ec8448fbb5c1")]
internal partial interface ICoreWebView2NavigationCompletedEventArgs
{
    void get_IsSuccess(out int isSuccess);
    void get_WebErrorStatus(out int webErrorStatus);
    void get_NavigationId(out ulong navigationId);
}

[GeneratedComInterface]
[Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651")]
internal partial interface ICoreWebView2NewWindowRequestedEventHandler
{
    void Invoke(ICoreWebView2? sender, ICoreWebView2NewWindowRequestedEventArgs? args);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("34acb11c-fc37-4418-9132-f9c21d1eafb9")]
internal partial interface ICoreWebView2NewWindowRequestedEventArgs
{
    void get_Uri(out string uri);
    void put_NewWindow(nint newWindow);
    void get_NewWindow(out nint newWindow);
    void put_Handled(int handled);
    void get_Handled(out int handled);
    void get_IsUserInitiated(out int isUserInitiated);
    void GetDeferral(out nint deferral);
    void get_WindowFeatures(out nint value);
}

// 下面是交给原生侧回调的托管实现 (CCW). 原生侧持有引用期间对象不会被回收.

[GeneratedComClass]
internal sealed partial class EnvironmentCompletedHandler(Action<int, ICoreWebView2Environment?> callback)
    : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    public void Invoke(int errorCode, ICoreWebView2Environment? createdEnvironment) => callback(errorCode, createdEnvironment);
}

[GeneratedComClass]
internal sealed partial class ControllerCompletedHandler(Action<int, ICoreWebView2Controller?> callback)
    : ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
    public void Invoke(int errorCode, ICoreWebView2Controller? createdController) => callback(errorCode, createdController);
}

[GeneratedComClass]
internal sealed partial class TrySuspendCompletedHandler(Action<int, bool> callback)
    : ICoreWebView2TrySuspendCompletedHandler
{
    public void Invoke(int errorCode, int isSuccessful) => callback(errorCode, isSuccessful != 0);
}

[GeneratedComClass]
internal sealed partial class ScriptAddedHandler(Action<int, string?> callback)
    : ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    public void Invoke(int errorCode, string? id) => callback(errorCode, id);
}

[GeneratedComClass]
internal sealed partial class DevToolsMethodCompletedHandler(Action<int, string?> callback)
    : ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    public void Invoke(int errorCode, string? returnObjectAsJson) => callback(errorCode, returnObjectAsJson);
}

[GeneratedComClass]
internal sealed partial class NavigationCompletedHandler(Action<ICoreWebView2NavigationCompletedEventArgs> callback)
    : ICoreWebView2NavigationCompletedEventHandler
{
    public void Invoke(ICoreWebView2? sender, ICoreWebView2NavigationCompletedEventArgs? args)
    {
        if (args != null) callback(args);
    }
}

[GeneratedComClass]
internal sealed partial class NewWindowRequestedHandler(Action<ICoreWebView2NewWindowRequestedEventArgs> callback)
    : ICoreWebView2NewWindowRequestedEventHandler
{
    public void Invoke(ICoreWebView2? sender, ICoreWebView2NewWindowRequestedEventArgs? args)
    {
        if (args != null) callback(args);
    }
}

// WebView2Loader 导出. Debug 跑的是 NuGet 包里的 WebView2Loader.dll (runtimes/win-x64/native);
// NativeAOT 发布时 csproj 用 DirectPInvoke 静态链接 WebView2LoaderStatic.lib.
internal static partial class WebView2Loader
{
    [LibraryImport("WebView2Loader", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateCoreWebView2EnvironmentWithOptions(
        string? browserExecutableFolder, string? userDataFolder, nint environmentOptions, nint environmentCreatedHandler);

    [LibraryImport("WebView2Loader", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetAvailableCoreWebView2BrowserVersionString(string? browserExecutableFolder, out nint versionInfo);
}
