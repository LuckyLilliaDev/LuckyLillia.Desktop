using LuckyLilliaDesktop.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>启动一个 LLBot 实例进程要的东西. 端口由 Desktop 分配, 以 --webui-port 传给 LLBot.</summary>
public sealed record LLBotLaunchOptions(string NodePath, string ScriptPath, int WebUiPort, string? HttpProxy, bool UseChinaCdn);

public sealed class InstanceStatusChangedEventArgs(string instance, ProcessStatus status, int? exitCode) : EventArgs
{
    public string Instance { get; } = instance;
    public ProcessStatus Status { get; } = status;
    public int? ExitCode { get; } = exitCode;
}

/// <summary>一个 LLBot 实例 = 一个 Node 进程, 按实例名管理.</summary>
public interface IProcessManager
{
    bool IsAnyProcessRunning { get; }
    IReadOnlyList<string> RunningInstances { get; }

    Task<bool> StartInstanceAsync(string instance, LLBotLaunchOptions options);
    Task StopInstanceAsync(string instance);
    Task StopAllAsync();
    void ForceKillAll();
    bool StartProcessOutsideJob(string fileName, string? workingDirectory = null);

    ProcessStatus GetStatus(string instance);
    int? GetPid(string instance);
    int? GetExitCode(string instance);

    event EventHandler<InstanceStatusChangedEventArgs>? StatusChanged;
}
