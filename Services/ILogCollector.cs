using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 日志收集服务接口
/// </summary>
public interface ILogCollector
{
    IObservable<LogEntry> LogStream { get; }
    IEnumerable<LogEntry> GetRecentLogs(int count);
    /// <summary>直接加一条 (Desktop 自己的日志走这里, 见 LogCollectorSink).</summary>
    void Add(LogEntry entry);
    void AttachProcess(string processName, Process process);
    void DetachProcess(string processName);
    /// <summary>processName 为 null 时清全部, 否则只清那个实例的.</summary>
    void ClearLogs(string? processName = null);
    int GetLogCount();
}

/// <summary>
/// 日志收集服务完整实现
/// </summary>
public class LogCollector : ILogCollector, IDisposable
{
    private const int MaxLogEntries = 1000;
    /// <summary>实例输出转写进 Serilog 时用的属性名, LogCollectorSink 靠它跳过这些行 (已经在收集器里了).</summary>
    public const string InstanceOutputProperty = "InstanceOutput";

    private readonly ILogger<LogCollector> _logger;
    // 实例的 stdout / stderr 读取线程和 Desktop 日志会并发推送, Rx 要求 OnNext 串行, 所以包一层同步
    private readonly Subject<LogEntry> _subject = new();
    private readonly ISubject<LogEntry> _logSubject;
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly Dictionary<string, CancellationTokenSource> _processCancellations = new();

    public IObservable<LogEntry> LogStream => _logSubject;

    public LogCollector(ILogger<LogCollector> logger)
    {
        _logger = logger;
        _logSubject = Subject.Synchronize(_subject);
    }

    public void Add(LogEntry entry)
    {
        _logQueue.Enqueue(entry);
        while (_logQueue.Count > MaxLogEntries)
        {
            _logQueue.TryDequeue(out _);
        }
        _logSubject.OnNext(entry);
    }

    public void AttachProcess(string processName, Process process)
    {
        if (_processCancellations.ContainsKey(processName))
        {
            _logger.LogWarning("进程 {Name} 已附加，先分离", processName);
            DetachProcess(processName);
        }

        var cts = new CancellationTokenSource();
        _processCancellations[processName] = cts;

        // 启动 stdout 读取线程
        Task.Run(() => ReadStreamAsync(processName, process.StandardOutput, "stdout", cts.Token), cts.Token);

        // 启动 stderr 读取线程
        Task.Run(() => ReadStreamAsync(processName, process.StandardError, "stderr", cts.Token), cts.Token);

        _logger.LogInformation("已附加进程 {Name} 的日志收集", processName);
    }

    public void DetachProcess(string processName)
    {
        if (_processCancellations.TryGetValue(processName, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _processCancellations.Remove(processName);
            _logger.LogInformation("已分离进程 {Name} 的日志收集", processName);
        }
    }

    private async Task ReadStreamAsync(string processName, StreamReader reader, string level, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var entry = new LogEntry(DateTime.Now, processName, level, line);
                Add(entry);

                // 同时记录到应用日志 (日志文件). 属性名固定为 InstanceOutputProperty, LogCollectorSink 据此不再回送
                if (level == "stderr")
                {
                    _logger.LogError("[{InstanceOutput}] {Message}", processName, line);
                }
                else
                {
                    _logger.LogInformation("[{InstanceOutput}] {Message}", processName, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 {Process} {Level} 流时出错", processName, level);
        }
    }

    public IEnumerable<LogEntry> GetRecentLogs(int count)
    {
        return _logQueue
            .TakeLast(count)
            .ToList();
    }

    public int GetLogCount()
    {
        return _logQueue.Count;
    }

    public void ClearLogs(string? processName = null)
    {
        if (processName == null)
        {
            _logQueue.Clear();
        }
        else
        {
            // ConcurrentQueue 不能原地过滤; 重建期间并发进来的几条可能丢, 对日志视图无所谓
            var keep = _logQueue.Where(e => e.ProcessName != processName).ToList();
            _logQueue.Clear();
            foreach (var entry in keep) _logQueue.Enqueue(entry);
        }
        _logger.LogInformation("日志已清空: {Scope}", processName ?? "全部");
    }

    public void Dispose()
    {
        foreach (var cts in _processCancellations.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _processCancellations.Clear();
        _subject.Dispose();
    }
}
