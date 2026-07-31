using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Arbiter;

public sealed class Sandbox : IDisposable
{
    private readonly IntPtr _job;
    private JOBOBJECT_EXTENDED_LIMIT_INFORMATION _extendedLimits;
    private JOBOBJECT_CPU_RATE_CONTROL_INFORMATION _cpuLimits;

    public Sandbox()
    {
        _job = CreateJobObject(IntPtr.Zero, "ANotherRccServiceArbiterLol");
        if (_job == IntPtr.Zero)
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

        _extendedLimits.BasicLimitInformation.LimitFlags = (uint)JobObjectLimitFlags.KILL_ON_JOB_CLOSE;
        ApplyExtendedLimits();
    }

    public Sandbox SetJobMemoryLimit(ulong bytes)
    {
        _extendedLimits.BasicLimitInformation.LimitFlags |= (uint)JobObjectLimitFlags.JOB_MEMORY;
        _extendedLimits.JobMemoryLimit = (UIntPtr)bytes;
        ApplyExtendedLimits();
        return this;
    }

    public Sandbox SetProcessMemoryLimit(ulong bytes)
    {
        _extendedLimits.BasicLimitInformation.LimitFlags |= (uint)JobObjectLimitFlags.PROCESS_MEMORY;
        _extendedLimits.ProcessMemoryLimit = (UIntPtr)bytes;
        ApplyExtendedLimits();
        return this;
    }

    public Sandbox SetActiveProcessLimit(uint limit)
    {
        _extendedLimits.BasicLimitInformation.LimitFlags |= (uint)JobObjectLimitFlags.ACTIVE_PROCESS;
        _extendedLimits.BasicLimitInformation.ActiveProcessLimit = limit;
        ApplyExtendedLimits();
        return this;
    }

    public Sandbox SetPriorityClass(ProcessPriorityClass priorityClass)
    {
        _extendedLimits.BasicLimitInformation.LimitFlags |= (uint)JobObjectLimitFlags.PRIORITY_CLASS;
        _extendedLimits.BasicLimitInformation.PriorityClass = (uint)priorityClass;
        ApplyExtendedLimits();
        return this;
    }

    public Sandbox SetCPUHardCap(ushort percent)
    {
        if (percent is 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "Use a value from 1 to 100.");

        _cpuLimits = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = (uint)(CpuRateControlFlags.ENABLE | CpuRateControlFlags.HARD_CAP),
            CpuRate = (uint)(percent * 100)
        };

        int length = Marshal.SizeOf(_cpuLimits);
        IntPtr ptr = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(_cpuLimits, ptr, false);

            if (!SetInformationJobObject(_job, JobObjectInfoType.CpuRateControlInformation, ptr, (uint)length))
            {
                throw new InvalidOperationException($"SetInformationJobObject(CpuRate) failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return this;
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.Handle))
            throw new InvalidOperationException($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero)
            CloseHandle(_job);
    }

    private void ApplyExtendedLimits()
    {
        int length = Marshal.SizeOf(_extendedLimits);
        IntPtr ptr = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(_extendedLimits, ptr, false);

            if (!SetInformationJobObject(_job, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)length))
            {
                throw new InvalidOperationException($"SetInformationJobObject(Extended) failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public Sandbox SetAffinity(ulong mask)
    {
        _extendedLimits.BasicLimitInformation.LimitFlags |= (uint)JobObjectLimitFlags.AFFINITY;
        _extendedLimits.BasicLimitInformation.Affinity = (UIntPtr)mask;

        ApplyExtendedLimits();

        return this;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
        CpuRateControlInformation = 15
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        JOB_MEMORY = 0x00000200,
        PROCESS_MEMORY = 0x00000100,
        ACTIVE_PROCESS = 0x00000008,
        PRIORITY_CLASS = 0x00000020,
        KILL_ON_JOB_CLOSE = 0x00002000,
        AFFINITY = 0x00000010
    }

    [Flags]
    private enum CpuRateControlFlags : uint
    {
        ENABLE = 0x1,
        HARD_CAP = 0x4
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

    [StructLayout(LayoutKind.Explicit)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        [FieldOffset(0)] public uint ControlFlags;
        [FieldOffset(4)] public uint CpuRate;
    }
}