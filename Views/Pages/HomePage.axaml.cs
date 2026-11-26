using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LuckyLilliaDesktop.Utils.WebView2;
using LuckyLilliaDesktop.ViewModels;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        WebView.NavigationCompleted += (_, e) => (DataContext as HomeViewModel)?.ReportNavigation(e.IsSuccess, e.WebErrorStatus);
        WebView.InitializationFailed += (_, ex) => (DataContext as HomeViewModel)?.ReportWebViewFailure(ex);
        // 打开统一 WebUI 前先放好登录 cookie, 免得桌面端内嵌视图还要输密码
        WebView.CookieProvider = url => (DataContext as HomeViewModel)?.WebUiLoginCookie(url);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WarmUpWebView();
    }

    // 浏览器进程启动要几百毫秒; LLBot 还在启动时就先把 environment 建好, WebUI 就绪后页面立刻出来
    private static void WarmUpWebView()
    {
        if (!WebView2Runtime.IsAvailable) return;
        try
        {
            WebView2Runtime.GetEnvironmentAsync()
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch
        {
            // 真正的失败在 WebView2Host 初始化时报给 ViewModel
        }
    }

    public void SetWebViewBackgroundMode(bool hidden) => WebView.SetBackgroundMode(hidden);

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as HomeViewModel)?.ClearPageError();
        WebView.Reload();
    }
}
