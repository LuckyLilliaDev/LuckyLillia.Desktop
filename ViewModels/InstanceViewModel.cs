using LuckyLilliaDesktop.Models;
using ReactiveUI;

namespace LuckyLilliaDesktop.ViewModels;

public enum InstancePhase
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

/// <summary>控制面板里的一个 LLBot 实例 (data/instances/&lt;Name&gt;), 对应最多一个进程.</summary>
public class InstanceViewModel : ViewModelBase
{
    public string Name { get; }

    public InstanceViewModel(string name)
    {
        Name = name;
    }

    private InstancePhase _phase = InstancePhase.Stopped;
    public InstancePhase Phase
    {
        get => _phase;
        set
        {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            // 页面关闭只对这一次运行有效, 实例停了再启动时 WebUI 照常显示
            if (value != InstancePhase.Running) IsWebUiClosed = false;
            RaiseDerived();
        }
    }

    // 用户在工具条上关掉了 WebUI 页面 (像关浏览器标签, 实例照常运行): WebView2 整个关掉, 浏览器进程退出; 可以重新打开
    private bool _isWebUiClosed;
    public bool IsWebUiClosed
    {
        get => _isWebUiClosed;
        set => this.RaiseAndSetIfChanged(ref _isWebUiClosed, value);
    }

    // Desktop 分配并以 --webui-port 传给 LLBot 的端口 (旧版 LLBot 自己换端口时是实测到的那个)
    private int? _port;
    public int? Port
    {
        get => _port;
        set
        {
            this.RaiseAndSetIfChanged(ref _port, value);
            RaiseDerived();
        }
    }

    private string? _url;
    public string? Url
    {
        get => _url;
        set => this.RaiseAndSetIfChanged(ref _url, value);
    }

    private int? _pid;
    public int? Pid
    {
        get => _pid;
        set => this.RaiseAndSetIfChanged(ref _pid, value);
    }

    private int? _exitCode;
    public int? ExitCode
    {
        get => _exitCode;
        set => this.RaiseAndSetIfChanged(ref _exitCode, value);
    }

    // 失败原因 / 退出说明, 遮罩上显示
    private string _message = string.Empty;
    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    private bool _isAutoStart;
    public bool IsAutoStart
    {
        get => _isAutoStart;
        set => this.RaiseAndSetIfChanged(ref _isAutoStart, value);
    }

    public bool IsRunning => Phase == InstancePhase.Running;
    public bool IsBusy => Phase is InstancePhase.Starting or InstancePhase.Stopping;
    public bool HasPort => Port != null;
    public string PortText => Port is int port ? $":{port}" : string.Empty;

    // 状态点颜色复用 StatusToColorConverter
    public ProcessStatus BotStatus => Phase switch
    {
        InstancePhase.Running => ProcessStatus.Running,
        InstancePhase.Starting => ProcessStatus.Starting,
        InstancePhase.Stopping => ProcessStatus.Stopping,
        InstancePhase.Failed => ProcessStatus.Error,
        _ => ProcessStatus.Stopped
    };

    private void RaiseDerived()
    {
        this.RaisePropertyChanged(nameof(IsRunning));
        this.RaisePropertyChanged(nameof(IsBusy));
        this.RaisePropertyChanged(nameof(BotStatus));
        this.RaisePropertyChanged(nameof(HasPort));
        this.RaisePropertyChanged(nameof(PortText));
    }
}

/// <summary>实例下拉框末尾的 "新建实例" 项, 不是实例.</summary>
public sealed class NewInstanceEntry
{
    public static readonly NewInstanceEntry Instance = new();
}
