using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;

namespace LuckyLilliaDesktop.ViewModels;

public class LogEntryViewModel : ViewModelBase
{
    public LogEntry LogEntry { get; }

    public string FormattedText
    {
        get
        {
            // 所有实例和 Desktop 自己的日志混在一起显示, 前面带来源; Desktop 的警告 / 错误级别标黄 / 标红
            var entry = LogEntry;
            var level = entry.ProcessName != LogCollectorSink.DesktopSource ? string.Empty : entry.Level switch
            {
                "WRN" => "\x1B[33mWRN\x1B[0m ",
                "ERR" or "FTL" => $"\x1B[31m{entry.Level}\x1B[0m ",
                _ => string.Empty,
            };
            return SanitizeText($"{entry.Timestamp:MM-dd HH:mm:ss} [{entry.ProcessName}] {level}{entry.Message}", preserveAnsi: true);
        }
    }

    public bool IsError => false;

    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);

    public string PlainText => AnsiEscapeRegex.Replace(FormattedText, "");

    public LogEntryViewModel(LogEntry logEntry)
    {
        LogEntry = logEntry;
    }

    private static string SanitizeText(string text, bool preserveAnsi = false)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // ANSI 转义序列：\x1B[...m — 保留完整序列
            if (preserveAnsi && c == '\x1B' && i + 1 < text.Length && text[i + 1] == '[')
            {
                sb.Append(c);
                i++;
                sb.Append(text[i]);
                while (++i < text.Length)
                {
                    sb.Append(text[i]);
                    if (text[i] == 'm') break;
                }
                continue;
            }

            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                continue;

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c);
                sb.Append(text[++i]);
                continue;
            }

            if (char.IsSurrogate(c))
                continue;

            if (c >= 0xE000 && c <= 0xF8FF)
                continue;

            sb.Append(c);
        }
        return sb.ToString();
    }
}

public class LogViewModel : ViewModelBase, IDisposable
{
    private readonly ILogCollector _logCollector;
    private readonly ILogger<LogViewModel> _logger;
    private readonly IDisposable _logSubscription;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public ObservableCollection<LogEntryViewModel> SelectedLogEntries { get; } = new();

    public event Action? ScrollToBottomRequested;
    public event Action? ClearSelectionRequested;

    private const int MaxLogs = 1000;

    public string Title => "终端日志";

    public void ScrollToBottom() => ScrollToBottomRequested?.Invoke();

    private bool _autoScroll = true;
    public bool AutoScroll
    {
        get => _autoScroll;
        set => this.RaiseAndSetIfChanged(ref _autoScroll, value);
    }

    private bool _hasSelection;
    public bool HasSelection
    {
        get => _hasSelection;
        set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    public ReactiveCommand<Unit, Unit> ClearLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }

    public LogViewModel(ILogCollector logCollector, ILogger<LogViewModel> logger)
    {
        _logCollector = logCollector;
        _logger = logger;

        // 先补上已有的 (启动日志、已在跑的实例), 再订阅新的
        RefreshRecentLogs();
        _logSubscription = _logCollector.LogStream
            .Buffer(TimeSpan.FromMilliseconds(200))
            .Where(batch => batch.Count > 0)
            .ObserveOnUiThread()
            .Subscribe(OnLogBatchReceived);

        SelectedLogEntries.CollectionChanged += (_, _) =>
        {
            HasSelection = SelectedLogEntries.Count > 0;
        };

        ClearLogsCommand = ReactiveCommand.Create(() =>
        {
            LogEntries.Clear();
            _logCollector.ClearLogs();
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        CopySelectedCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedLogEntries.Count == 0) return;

            var text = string.Join(Environment.NewLine,
                SelectedLogEntries.Select(e => e.PlainText));

            var clipboard = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;

            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                _logger.LogInformation("已复制 {Count} 条日志", SelectedLogEntries.Count);
            }

            SelectedLogEntries.Clear();
            ClearSelectionRequested?.Invoke();
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        ClearSelectionCommand = ReactiveCommand.Create(() =>
        {
            SelectedLogEntries.Clear();
            ClearSelectionRequested?.Invoke();
        }, outputScheduler: AvaloniaUiScheduler.Instance);
    }

    private void OnLogBatchReceived(System.Collections.Generic.IList<LogEntry> batch)
    {
        if (_isUIUpdatesPaused) return;

        foreach (var logEntry in batch)
        {
            LogEntries.Add(new LogEntryViewModel(logEntry));
        }

        while (LogEntries.Count > MaxLogs)
        {
            LogEntries.RemoveAt(0);
        }

        if (AutoScroll && !HasSelection)
        {
            ScrollToBottomRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _logSubscription.Dispose();
    }

    private bool _isUIUpdatesPaused;

    public void PauseUIUpdates()
    {
        if (_isUIUpdatesPaused) return;
        _isUIUpdatesPaused = true;
    }

    public void ResumeUIUpdates()
    {
        if (!_isUIUpdatesPaused) return;
        _isUIUpdatesPaused = false;
        
        // 恢复时重新加载最近的日志
        RefreshRecentLogs();
    }
    
    private void RefreshRecentLogs()
    {
        LogEntries.Clear();
        foreach (var log in _logCollector.GetRecentLogs(MaxLogs))
        {
            LogEntries.Add(new LogEntryViewModel(log));
        }
        
        if (AutoScroll)
        {
            ScrollToBottomRequested?.Invoke();
        }
    }
}
