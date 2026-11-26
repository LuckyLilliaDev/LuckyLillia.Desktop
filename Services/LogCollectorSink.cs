using System.Collections.Generic;
using LuckyLilliaDesktop.Models;
using Serilog.Core;
using Serilog.Events;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// Serilog sink: 把 Desktop 自己的日志也送进 <see cref="ILogCollector"/>, 终端日志里和各实例的输出一起显示.
/// Serilog 在 DI 之前就建好了, 所以收集器后接 (<see cref="Attach"/>), 之前的日志先缓存着, 接上后按顺序补发.
/// </summary>
public sealed class LogCollectorSink : ILogEventSink
{
    public const string DesktopSource = "Desktop";
    private const int MaxPending = 500;

    private readonly object _lock = new();
    private readonly Queue<LogEntry> _pending = new();
    private ILogCollector? _collector;

    public void Emit(LogEvent logEvent)
    {
        // 实例的每行输出 LogCollector 也会写进 Serilog (为了进日志文件), 这些已经在收集器里了, 不再回送
        if (logEvent.Properties.ContainsKey(LogCollector.InstanceOutputProperty)) return;

        var message = logEvent.RenderMessage();
        if (logEvent.Exception is { } ex) message += $" ({ex.GetType().Name}: {ex.Message})";
        var entry = new LogEntry(logEvent.Timestamp.LocalDateTime, DesktopSource, LevelName(logEvent.Level), message);

        ILogCollector? collector;
        lock (_lock)
        {
            collector = _collector;
            if (collector == null)
            {
                _pending.Enqueue(entry);
                while (_pending.Count > MaxPending) _pending.Dequeue();
                return;
            }
        }
        collector.Add(entry);
    }

    public void Attach(ILogCollector collector)
    {
        lock (_lock)
        {
            while (_pending.TryDequeue(out var entry)) collector.Add(entry);
            _collector = collector;
        }
    }

    private static string LevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        _ => "FTL",
    };
}
