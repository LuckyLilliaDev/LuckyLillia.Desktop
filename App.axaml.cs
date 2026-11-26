using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.ViewModels;
using LuckyLilliaDesktop.Views;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ConfigureServices();
        
        // 注册进程退出事件，确保清理子进程
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;
        
        // 后台检查开机自启：清理失效的 HKCU Run 值，并迁移旧版本
        // 创建的同名计划任务。新的自启方式仍只使用当前用户注册表。
        _ = Task.Run(() =>
        {
            var cleanup = StartupManager.TryCleanupInvalidRegistryStartup();
            if (!cleanup.Success)
            {
                Log.Warning("检查或迁移开机自启配置失败: {ErrorMessage}", cleanup.ErrorMessage);
            }
        });
        
        if (DevMode.IsEnabled)
        {
            Log.Warning("[DEV] 开发模式已开启 (app_settings.json 的 dev): 启动时不校验 Auth Token, 并向 LLBot 透传 --dev");
        }

        Log.Information("应用初始化完成");
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Log.Information("检测到进程退出，清理子进程...");
        CleanupChildProcesses();
    }

    private async void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        await ExitApplicationAsync();
    }

    private void CleanupChildProcesses()
    {
        try
        {
            var processManager = Services?.GetService<IProcessManager>();
            
            processManager?.ForceKill();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理子进程失败");
        }
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // 配置 Serilog - 每次启动创建新日志文件，限制单文件大小并清理旧日志
        CleanupOldLogs("logs", maxAgeDays: 7, maxTotalFiles: 50);
        var logFileName = $"logs/{DateTime.Now:yyyyMMdd_HHmmss}.log";
        // Desktop 自己的日志也进终端日志; 收集器要等 DI 建好再接上, 之前的日志先缓存
        var collectorSink = new LogCollectorSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFileName,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                fileSizeLimitBytes: 10 * 1024 * 1024,  // 单文件最大 10MB
                rollOnFileSizeLimit: true)              // 超限后自动创建新文件 (_001, _002...)
            .WriteTo.Sink(collectorSink)
            .CreateLogger();
        
        // 设置为全局 logger
        Log.Logger = logger;

        // 核心服务（单例）
        services.AddSingleton<IConfigManager, ConfigManager>();
        services.AddSingleton<ILogCollector, LogCollector>();
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton<ISystemTimeChecker, SystemTimeChecker>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        services.AddSingleton<IUpdateStateService, UpdateStateService>();

        // ViewModels（瞬态）
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LogViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AboutViewModel>();

        // 日志
        services.AddLogging(builder =>
        {
            builder.AddSerilog(logger, dispose: true);
        });

        Services = services.BuildServiceProvider();
        collectorSink.Attach(Services.GetRequiredService<ILogCollector>());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainVM = Services.GetRequiredService<MainWindowViewModel>();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainVM
                };

                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;

                // macOS: 主动显示并激活窗口
                if (PlatformHelper.IsMacOS)
                {
                    // 临时设置 Topmost 确保窗口出现在最前面
                    desktop.MainWindow.Topmost = true;
                    desktop.MainWindow.Show();
                    desktop.MainWindow.Activate();
                    // 恢复 Topmost 状态
                    desktop.MainWindow.Topmost = false;
                }
            }

            Log.Information("框架初始化完成，主窗口已创建");
            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "框架初始化失败");
            System.IO.File.WriteAllText("startup_error.log", $"{DateTime.Now}: {ex}");
            throw;
        }
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        e.Cancel = true;
        await ExitApplicationAsync();
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void ShowWindow_Click(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private async void Exit_Click(object? sender, EventArgs e)
    {
        await ExitApplicationAsync();
    }

    /// <summary>
    /// 统一的应用退出方法
    /// </summary>
    private Task? _exitTask;
    public Task ExitApplicationAsync() => _exitTask ??= ExitCoreAsync();

    private async Task ExitCoreAsync()
    {
        Log.Information("应用正在退出，等待 LLBot 停止所有实例并清理原生会话");
        var processManager = Services.GetService<IProcessManager>();
        // WebView2 和 LLBot 互不相干, 并行收尾 (WebView 要先于进程退出正常关掉, 见 MainWindow.ShutdownWebViewAsync)
        var webViewShutdown = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow mainWindow
            ? mainWindow.ShutdownWebViewAsync()
            : Task.CompletedTask;
        try
        {
            if (processManager != null) await processManager.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "正常退出失败，强制清理 LLBot 进程树");
            processManager?.ForceKill();
        }
        try { await webViewShutdown; }
        catch (Exception ex) { Log.Warning(ex, "关闭 WebView2 失败"); }
        Environment.Exit(0);
    }

    /// <summary>
    /// 启动时清理旧日志文件：超过 maxAgeDays 天的删除，总文件数超过 maxTotalFiles 时删除最旧的
    /// </summary>
    private static void CleanupOldLogs(string logDir, int maxAgeDays, int maxTotalFiles)
    {
        try
        {
            if (!System.IO.Directory.Exists(logDir)) return;

            var files = new System.IO.DirectoryInfo(logDir)
                .GetFiles("*.log")
                .OrderBy(f => f.CreationTime)
                .ToList();

            var cutoff = DateTime.Now.AddDays(-maxAgeDays);

            // 删除超龄文件
            foreach (var file in files.Where(f => f.CreationTime < cutoff).ToList())
            {
                try { file.Delete(); files.Remove(file); } catch { }
            }

            // 如果仍超出总数限制，删除最旧的
            while (files.Count > maxTotalFiles)
            {
                try { files[0].Delete(); } catch { }
                files.RemoveAt(0);
            }
        }
        catch { }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }
    }
}
