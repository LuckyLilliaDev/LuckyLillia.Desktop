using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    // 下拉框末尾的 "新建实例" 不是实例: 直接把控件拨回当前实例 (绕开两向绑定不推相同值的问题), 再走新建流程
    private void InstancePicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InstancePicker.SelectedItem is not NewInstanceEntry || DataContext is not HomeViewModel vm) return;
        Dispatcher.UIThread.Post(() =>
        {
            InstancePicker.SelectedItem = vm.SelectedInstance;
            vm.RequestNewInstance();
        });
    }
}
