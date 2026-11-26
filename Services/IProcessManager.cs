using LuckyLilliaDesktop.Models;
using System;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>启动 LLBot 统一管理进程的参数. 端口由 Desktop 确认空闲后以 --webui-port 传入.</summary>
public sealed record LLBotLaunchOptions(string NodePath, string ScriptPath, int WebUiPort, string? HttpProxy, bool UseChinaCdn);

/// <summary>启动结果: 失败时 <see cref="Error"/> 是给用户看的原因 (界面直接显示, 日志里也有).</summary>
public sealed record ProcessStartResult(bool Started, string? Error = null)
{
    public static readonly ProcessStartResult Ok = new(true);
    public static ProcessStartResult Failed(string error) => new(false, error);
}

public sealed class ProcessStatusChangedEventArgs(ProcessStatus status, int? exitCode, bool requested = false) : EventArgs
{
    public ProcessStatus Status { get; } = status;
    public int? ExitCode { get; } = exitCode;
    /// <summary>
    /// 这次状态变化是 <see cref="IProcessManager.StopAsync"/> 主动停止造成的 (Stopping / 停止后的 Stopped), 不是进程自己退出.
    /// 发起停止的一方自己收尾; 订阅方再拿它当 "进程退出" 处理会误伤: 事件被 Post 到 UI 线程时, 新进程可能已经启动了.
    /// </summary>
    public bool Requested { get; } = requested;
}

/// <summary>管理唯一的 LLBot 进程 (llbot.js -> llbot-manager.js -> 各实例子进程).</summary>
public interface IProcessManager
{
    bool IsRunning { get; }
    ProcessStatus Status { get; }
    int? Pid { get; }
    int? ExitCode { get; }

    Task<ProcessStartResult> StartAsync(LLBotLaunchOptions options);
    Task StopAsync();
    void ForceKill();
    bool StartProcessOutsideJob(string fileName, string? workingDirectory = null);

    event EventHandler<ProcessStatusChangedEventArgs>? StatusChanged;
}
