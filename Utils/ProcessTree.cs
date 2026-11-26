using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 进程及其所有子孙进程的 PID (Windows, Toolhelp32 快照). LLBot 9 起 llbot.js 只是启动器, 真正监听 WebUI 端口的是它
/// 拉起的子进程 llbot-core.js, 所以核对端口归属时要认整棵进程树, 只认启动器 PID 会一直等到超时.
/// 非 Windows 或读取失败时只返回根 PID 本身.
/// </summary>
internal static class ProcessTree
{
    public static HashSet<int> WithDescendants(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        if (!OperatingSystem.IsWindows()) return result;
        Dictionary<int, List<int>> children;
        try
        {
            children = ReadChildren();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return result;
        }

        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!children.TryGetValue(parent, out var kids)) continue;
            var parentStart = StartTime(parent);
            foreach (var kid in kids)
            {
                // 父 PID 只是个数字: 原父进程退出后 PID 被复用, 老的孤儿进程会挂到新进程名下. 子进程必须比父进程晚启动
                if (parentStart is DateTime ps && StartTime(kid) is DateTime ks && ks < ps) continue;
                if (result.Add(kid)) queue.Enqueue(kid);
            }
        }
        return result;
    }

    private static DateTime? StartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly nint InvalidHandle = -1;

    // PROCESSENTRY32W: dwSize, cntUsage, th32ProcessID, th32DefaultHeapID (ULONG_PTR), th32ModuleID, cntThreads,
    // th32ParentProcessID, pcPriClassBase, dwFlags, szExeFile[260]
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint hObject);

    private static Dictionary<int, List<int>> ReadChildren()
    {
        var map = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle || snapshot == 0) return map;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;
            do
            {
                var pid = (int)entry.th32ProcessID;
                var parent = (int)entry.th32ParentProcessID;
                if (pid != 0 && pid != parent)
                {
                    if (!map.TryGetValue(parent, out var list)) map[parent] = list = new List<int>();
                    list.Add(pid);
                }
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }
}
