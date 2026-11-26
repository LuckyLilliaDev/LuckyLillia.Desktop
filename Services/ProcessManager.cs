using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// LLBot 进程管理: Desktop 只拉起一个 llbot.js 进程 (多实例由其内部的 llbot-manager.js 统一管理).
/// 优雅停止 = 向它的控制台发 Ctrl+C (管理器收到 SIGINT 后通过 IPC 在 25 秒内停掉所有实例并退出),
/// 超时 (35 秒) 再进程树兜底; Windows Job Object 兜底 Desktop 崩溃; --parent-pid 让 LLBot 在 Desktop 消失时自己退出.
/// </summary>
public partial class ProcessManager : IProcessManager, IDisposable
{
    public const string LogSourceName = "LLBot";

    private sealed class Entry
    {
        public required Process Process { get; init; }
        public ProcessStatus Status { get; set; }
        public int? ExitCode { get; set; }
        public CancellationTokenSource MonitorCts { get; } = new();
    }

    private readonly ILogger<ProcessManager> _logger;
    private readonly ILogCollector _logCollector;
    private readonly object _entryLock = new();
    private Entry? _entry;
    private int? _lastExitCode;
    // 启动 / 停止串行化, 状态变化才不会交错
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    // Job Object 句柄，用于自动清理子进程
    private static IntPtr _jobHandle = IntPtr.Zero;

    public event EventHandler<ProcessStatusChangedEventArgs>? StatusChanged;

    public ProcessManager(ILogger<ProcessManager> logger, ILogCollector logCollector)
    {
        _logger = logger;
        _logCollector = logCollector;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            InitializeJobObject();
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_entryLock) return _entry is { Status: ProcessStatus.Running or ProcessStatus.Starting };
        }
    }

    public ProcessStatus Status
    {
        get
        {
            lock (_entryLock) return _entry?.Status ?? ProcessStatus.Stopped;
        }
    }

    public int? Pid
    {
        get
        {
            lock (_entryLock)
            {
                if (_entry == null) return null;
                try { return _entry.Process.HasExited ? null : _entry.Process.Id; }
                catch (InvalidOperationException) { return null; }
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_entryLock) return _entry?.ExitCode ?? _lastExitCode;
        }
    }

    private Entry? GetEntry()
    {
        lock (_entryLock) return _entry;
    }

    private void SetStatus(Entry entry, ProcessStatus status, bool requested = false)
    {
        entry.Status = status;
        StatusChanged?.Invoke(this, new ProcessStatusChangedEventArgs(status, entry.ExitCode, requested));
    }

    /// <summary>
    /// 初始化 Job Object，确保主进程被杀死时自动清理所有子进程
    /// </summary>
    private void InitializeJobObject()
    {
        try
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero)
            {
                _logger.LogWarning("创建 Job Object 失败");
                return;
            }

            // 设置 Job Object 属性：当 Job 关闭时杀死所有进程
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, extendedInfoPtr, false);

                if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length))
                {
                    _logger.LogWarning("设置 Job Object 信息失败");
                    CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }

            var currentProcess = Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(_jobHandle, currentProcess.Handle))
            {
                _logger.LogWarning("将当前进程加入 Job Object 失败");
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
                return;
            }

            _logger.LogInformation("Job Object 初始化成功，子进程将在主进程退出时自动清理");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化 Job Object 失败");
        }
    }

    // 代理 URL 可能带认证信息 (http://user:pass@host:port), 日志里隐去凭证部分
    private static readonly Regex ProxyCredentialRegex = new(@"://[^@/\s]+@");

    private static string MaskProxyCredentials(string proxy) => ProxyCredentialRegex.Replace(proxy, "://***@");

    private static readonly string[] ProxyEnvNames = { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" };

    public async Task<ProcessStartResult> StartAsync(LLBotLaunchOptions options)
    {
        await _lifecycle.WaitAsync();
        try { return await StartCoreAsync(options); }
        finally { _lifecycle.Release(); }
    }

    // 失败一律带上原因返回: 调用方直接显示给用户, 不再只有一句 "启动失败，请查看日志"
    private ProcessStartResult Fail(string message, Exception? ex = null)
    {
        if (ex != null) _logger.LogError(ex, "{Message}", message);
        else _logger.LogError("{Message}", message);
        return ProcessStartResult.Failed(message);
    }

    private async Task<ProcessStartResult> StartCoreAsync(LLBotLaunchOptions options)
    {
        var status = Status;
        if (status is ProcessStatus.Running or ProcessStatus.Starting or ProcessStatus.Stopping)
        {
            var pid = Pid;
            return Fail($"LLBot 已有进程 (PID {pid?.ToString() ?? "未知"}, 状态 {status})，没有再启动一个");
        }

        if (!File.Exists(options.NodePath))
            return Fail($"Node.js 可执行文件不存在: {Path.GetFullPath(options.NodePath)}");

        if (!File.Exists(options.ScriptPath))
            return Fail($"LLBot 脚本不存在: {Path.GetFullPath(options.ScriptPath)}");

        var workingDir = Path.GetDirectoryName(Path.GetFullPath(options.ScriptPath))!;
        var scriptFileName = Path.GetFileName(options.ScriptPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(options.NodePath),
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 独立的隐藏控制台, 停止时往这个控制台发 Ctrl+C
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.Environment["NODE_SKIP_PLATFORM_CHECK"] = "1";
        startInfo.Environment["FORCE_COLOR"] = "3";
        if (!string.IsNullOrWhiteSpace(options.HttpProxy))
        {
            _logger.LogInformation("已设置 HTTP 代理环境变量: {Proxy}", MaskProxyCredentials(options.HttpProxy));
            foreach (var name in ProxyEnvNames) startInfo.Environment[name] = options.HttpProxy.Trim();
        }

        startInfo.ArgumentList.Add("--enable-source-maps");
        startInfo.ArgumentList.Add("--disable-warning=ExperimentalWarning");
        startInfo.ArgumentList.Add(scriptFileName);

        // 传给 LLBot 脚本的参数 (都放在 -- 之后). 不再传 --instance, 多实例由 llbot-manager.js 统一管理.
        startInfo.ArgumentList.Add("--");
        // Desktop 没了 LLBot 自己退出 (llbot.js / llbot-manager.js)
        startInfo.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");
        startInfo.ArgumentList.Add("--webui-port");
        startInfo.ArgumentList.Add(options.WebUiPort.ToString());
        if (options.UseChinaCdn)
        {
            startInfo.ArgumentList.Add("--cdn");
            startInfo.ArgumentList.Add("china");
        }
        if (DevMode.IsEnabled)
        {
            startInfo.ArgumentList.Add(DevMode.Argument);
        }

        _logger.LogInformation("启动 LLBot: {Node} {Args}", options.NodePath, string.Join(" ", startInfo.ArgumentList));

        // 子进程继承本进程的 "忽略 Ctrl+C" 标志 (SetConsoleCtrlHandler(NULL, TRUE); 有些终端 / 启动器会给拉起的程序带上).
        // 带着它的 LLBot 收不到 Ctrl+C, 停止只能等超时强杀, 所以起进程前先清掉.
        if (PlatformHelper.IsWindows) SetConsoleCtrlHandler(IntPtr.Zero, false);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return Fail($"启动 LLBot 进程失败: {ex.Message} (Node: {startInfo.FileName}, 工作目录: {workingDir})", ex);
        }

        if (process == null)
            return Fail($"启动 LLBot 进程失败: 系统没有返回进程 (Node: {startInfo.FileName})");

        var entry = new Entry { Process = process, Status = ProcessStatus.Starting };
        lock (_entryLock)
        {
            _entry = entry;
            _lastExitCode = null;
        }
        SetStatus(entry, ProcessStatus.Starting);

        // 等一小段时间确认没有立即退出 (参数错 / 已有管理进程占用数据目录)
        await Task.Delay(500);
        if (process.HasExited)
        {
            entry.ExitCode = process.ExitCode;
            var tail = await LogExitedProcessOutputAsync(process);
            lock (_entryLock)
            {
                _lastExitCode = entry.ExitCode;
                if (ReferenceEquals(_entry, entry)) _entry = null;
            }
            SetStatus(entry, ProcessStatus.Error);
            process.Dispose();
            var reason = $"LLBot 启动后立即退出 (退出码 {entry.ExitCode})";
            return Fail(string.IsNullOrEmpty(tail) ? $"{reason}，没有任何输出" : $"{reason}:\n{tail}");
        }

        _logCollector.AttachProcess(LogSourceName, process);
        SetStatus(entry, ProcessStatus.Running);
        _ = MonitorProcessAsync(entry);
        _logger.LogInformation("LLBot 已启动, PID: {Pid}", process.Id);
        return ProcessStartResult.Ok;
    }

    /// <summary>把立即退出的进程的全部输出记进日志, 返回最后几行 (优先 stderr, 去掉 ANSI 颜色) 给界面显示.</summary>
    private async Task<string> LogExitedProcessOutputAsync(Process process)
    {
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            var stdout = stdoutTask.Result.Trim();
            var stderr = stderrTask.Result.Trim();

            if (!string.IsNullOrEmpty(stdout))
                _logger.LogError("[{Source}] stdout: {Output}", LogSourceName, stdout);
            if (!string.IsNullOrEmpty(stderr))
                _logger.LogError("[{Source}] stderr: {Error}", LogSourceName, stderr);
            return Tail(string.IsNullOrEmpty(stderr) ? stdout : stderr, 5);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 LLBot 退出输出失败");
            return string.Empty;
        }
    }

    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;]*[A-Za-z]");

    private static string Tail(string text, int lines)
    {
        var all = AnsiEscapeRegex.Replace(text, "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }

    private async Task MonitorProcessAsync(Entry entry)
    {
        try
        {
            await entry.Process.WaitForExitAsync(entry.MonitorCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "监控 LLBot 进程时出错");
            return;
        }

        // 主动停止的路径由 StopCoreAsync 收尾, 这里只处理自己退出的
        if (entry.Status == ProcessStatus.Stopping) return;
        try { entry.ExitCode = entry.Process.ExitCode; } catch { }
        _logger.LogWarning("LLBot 进程已退出, ExitCode: {ExitCode}", entry.ExitCode);
        _logCollector.DetachProcess(LogSourceName);
        lock (_entryLock)
        {
            _lastExitCode = entry.ExitCode;
            if (ReferenceEquals(_entry, entry)) _entry = null;
        }
        SetStatus(entry, ProcessStatus.Stopped);
        entry.Process.Dispose();
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var entry = GetEntry();
        if (entry == null) return;

        entry.MonitorCts.Cancel();
        SetStatus(entry, ProcessStatus.Stopping, requested: true);

        var process = entry.Process;
        try
        {
            if (!process.HasExited)
            {
                var pid = process.Id;
                if (RequestGracefulStop(pid))
                {
                    try
                    {
                        // 管理器收到 SIGINT 后会在 25 秒内逐一停止所有实例, 给足 35 秒余量
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("LLBot 没有在 35 秒内退出，强制清理其进程树");
                    }
                }
                if (!process.HasExited) await KillProcessTreeAsync(pid);
            }
            try { entry.ExitCode = process.HasExited ? process.ExitCode : null; } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止 LLBot 时出错");
        }
        finally
        {
            // 退出后再分离, 关停期间 LLBot 打的日志也要收
            _logCollector.DetachProcess(LogSourceName);
            lock (_entryLock)
            {
                _lastExitCode = entry.ExitCode;
                if (ReferenceEquals(_entry, entry)) _entry = null;
            }
            SetStatus(entry, ProcessStatus.Stopped, requested: true);
            process.Dispose();
            _logger.LogInformation("LLBot 已停止");
        }
    }

    // Windows: 附到子进程的隐藏控制台上发 Ctrl+C (Node 把它当 SIGINT). 自己先忽略 Ctrl+C, 否则这一下也会打到 Desktop.
    // Unix: SIGTERM. 返回 false 表示没发出去, 调用方直接杀进程树.
    private bool RequestGracefulStop(int pid)
    {
        if (!PlatformHelper.IsWindows)
        {
            try
            {
                using var kill = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}") { UseShellExecute = false, CreateNoWindow = true });
                kill?.WaitForExit(5000);
                return kill is { ExitCode: 0 };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "向进程 {Pid} 发送 SIGTERM 失败", pid);
                return false;
            }
        }

        return SendConsoleCtrlC(pid);
    }

    private bool SendConsoleCtrlC(int pid)
    {
        // Desktop 自己带着控制台时 (dotnet run) 先脱离, 事后再接回父进程的控制台
        var hadOwnConsole = false;
        if (!AttachConsole((uint)pid))
        {
            if (Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED) return LogCtrlFailure(pid, "AttachConsole");
            hadOwnConsole = true;
            FreeConsole();
            if (!AttachConsole((uint)pid)) return LogCtrlFailure(pid, "AttachConsole");
        }

        var sent = false;
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
            if (!sent) LogCtrlFailure(pid, "GenerateConsoleCtrlEvent");
            // 事件是异步投递的, 等它在本进程里被 (忽略地) 处理完再解除忽略
            Thread.Sleep(200);
        }
        finally
        {
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            if (hadOwnConsole) AttachConsole(ATTACH_PARENT_PROCESS);
        }
        return sent;
    }

    private bool LogCtrlFailure(int pid, string step)
    {
        _logger.LogWarning("向进程 {Pid} 发送 Ctrl+C 失败 ({Step}, error={Error})，改为强制结束", pid, step, Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>
    /// 等待进程完全退出
    /// </summary>
    private static async Task WaitForProcessExitAsync(int pid, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                Process.GetProcessById(pid);
                await Task.Delay(200);
            }
            catch (ArgumentException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 异步杀死进程树并等待完全退出
    /// </summary>
    private static async Task KillProcessTreeAsync(int pid, int timeoutMs = 10000)
    {
        try
        {
            var psi = PlatformHelper.IsWindows
                ? new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
                : new ProcessStartInfo("kill", $"-KILL {pid}");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var killProcess = Process.Start(psi);
            if (killProcess != null) await killProcess.WaitForExitAsync();

            await WaitForProcessExitAsync(pid, timeoutMs);
        }
        catch
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 快速强制终止 LLBot 进程树（用于应用退出兜底）
    /// </summary>
    public void ForceKill()
    {
        var entry = GetEntry();
        if (entry == null) return;
        try
        {
            if (entry.Process.HasExited) return;
            var pid = entry.Process.Id;
            _logger.LogInformation("强制终止 LLBot, PID: {Pid}", pid);
            KillProcessTree(pid);
        }
        catch { }
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            var psi = PlatformHelper.IsWindows
                ? new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
                : new ProcessStartInfo("kill", $"-KILL {pid}");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using var killProcess = Process.Start(psi);
            killProcess?.WaitForExit(5000);
        }
        catch
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 启动一个脱离 Job Object 的进程，使其不会随主进程退出而被终止（仅 Windows）
    /// </summary>
    public bool StartProcessOutsideJob(string fileName, string? workingDirectory = null)
    {
        if (!PlatformHelper.IsWindows)
        {
            _logger.LogWarning("StartProcessOutsideJob 仅支持 Windows 平台");
            return false;
        }

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var cmdLine = $"cmd.exe /c \"{fileName}\"";

        var created = CreateProcess(
            null,
            cmdLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_CONSOLE,
            IntPtr.Zero,
            workingDirectory,
            ref si,
            out var pi);

        if (!created)
        {
            _logger.LogError("CreateProcess 失败, error={Error}", Marshal.GetLastWin32Error());
            return false;
        }

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        _logger.LogInformation("已启动脱离 Job 的进程: PID={Pid}, cmd={Cmd}", pi.dwProcessId, cmdLine);
        return true;
    }

    public void Dispose()
    {
        var entry = GetEntry();
        if (entry != null)
        {
            entry.MonitorCts.Cancel();
            entry.MonitorCts.Dispose();
            entry.Process.Dispose();
        }

        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    #region Windows API

    private const int ERROR_ACCESS_DENIED = 5;
    private const uint CTRL_C_EVENT = 0;
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;

    #endregion
}
