using Arbiter;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

static class Health
{
    private static readonly object Sync = new();
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly Process Self = Process.GetCurrentProcess();
    private static readonly Timer Timer;

    private static TimeSpan _lastCpuTime = Self.TotalProcessorTime;
    private static DateTime _lastSampleUtc = DateTime.UtcNow;
    private static long _lastBytesReceived = GetTotalBytesReceived();
    private static long _lastBytesSent = GetTotalBytesSent();

    public static float AvailablePhysicalMemoryGigabytes { get; private set; }
    public static float TotalPhysicalMemoryGigabytes { get; private set; }
    public static float CpuUsage { get; private set; }
    public static float DownloadSpeedKilobytesPerSecond { get; private set; }
    public static float UploadSpeedKilobytesPerSecond { get; private set; }
    public static int LogicalProcessorCount { get; private set; } = Environment.ProcessorCount;
    public static int ProcessorCount { get; private set; } = Environment.ProcessorCount;
    public static int RccServiceProcesses { get; private set; }
    public static string? RccVersion { get; private set; }

    private const int RelationProcessorCore = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
    {
        public int Relationship;
        public int Size;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref int returnedLength);

    public static int GetPhysicalCoreCount()
    {
        int length = 0;

        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);

        IntPtr buffer = Marshal.AllocHGlobal(length);

        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) {
                return Environment.ProcessorCount;
            }

            int cores = 0;
            IntPtr ptr = buffer;
            IntPtr end = IntPtr.Add(buffer, length);

            while (ptr.ToInt64() < end.ToInt64())
            {
                var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(ptr);

                if (info.Relationship == RelationProcessorCore)
                    cores++;

                ptr = IntPtr.Add(ptr, info.Size);
            }

            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static Health()
    {
        Refresh();
        Timer = new Timer(_ => RefreshSafe(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static void RefreshSafe()
    {
        try
        {
            Refresh();
        }
        catch {}
    }

    public static void Refresh()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            var elapsedSeconds = Math.Max((now - _lastSampleUtc).TotalSeconds, 0.001);

            var currentCpu = Self.TotalProcessorTime;
            var cpuDeltaSeconds = (currentCpu - _lastCpuTime).TotalSeconds;
            CpuUsage = (float)((cpuDeltaSeconds / (elapsedSeconds * Environment.ProcessorCount)) * 100.0);
            CpuUsage = Math.Clamp(CpuUsage, 0f, 100f);
            _lastCpuTime = currentCpu;

            var bytesReceived = GetTotalBytesReceived();
            var bytesSent = GetTotalBytesSent();

            DownloadSpeedKilobytesPerSecond = (float)((bytesReceived - _lastBytesReceived) / 1024.0 / elapsedSeconds);
            UploadSpeedKilobytesPerSecond = (float)((bytesSent - _lastBytesSent) / 1024.0 / elapsedSeconds);

            _lastBytesReceived = bytesReceived;
            _lastBytesSent = bytesSent;
            _lastSampleUtc = now;

            var (availableGb, totalGb) = GetMemoryGb();
            AvailablePhysicalMemoryGigabytes = availableGb;
            TotalPhysicalMemoryGigabytes = totalGb;

            LogicalProcessorCount = Environment.ProcessorCount;
            ProcessorCount = GetPhysicalCoreCount();

            RccServiceProcesses = Process.GetProcessesByName(Configuration.GetStringFlag("FStringRCCServiceName")).Length;
            RccVersion = GetFirstMatchingProcessVersion(Configuration.GetStringFlag("FStringRCCServiceName"));
        }
    }

    public static float GetRAM()
    {
        var (availableGb, totalGb) = GetMemoryGb();
        if (totalGb <= 0f)
            return 0f;

        return (1f - (availableGb / totalGb)) * 100f;
    }

    private static (float availableGb, float totalGb) GetMemoryGb()
    {
        if (IsWindows)
            return GetMemoryGbWindows();

        return GetMemoryGbLinux();
    }

    private static (float availableGb, float totalGb) GetMemoryGbWindows()
    {
        var mem = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0)
            return (0f, 0f);

        var availableGb = mem.ullAvailPhys / 1024f / 1024f / 1024f;
        var totalGb = mem.ullTotalPhys / 1024f / 1024f / 1024f;
        return (availableGb, totalGb);
    }

    private static (float availableGb, float totalGb) GetMemoryGbLinux()
    {
        long totalBytes = 0;
        long availableBytes = 0;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:"))
                totalBytes = ParseKb(line);
            else if (line.StartsWith("MemAvailable:"))
                availableBytes = ParseKb(line);

            if (totalBytes > 0 && availableBytes > 0)
                break;
        }

        if (totalBytes <= 0)
            return (0f, 0f);

        return (availableBytes / 1024f / 1024f / 1024f, totalBytes / 1024f / 1024f / 1024f);
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(parts[1]) * 1024;
    }

    private static long GetTotalBytesReceived()
    {
        long total = 0;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                total += ni.GetIPStatistics().BytesReceived;
            }
            catch {}
        }

        return total;
    }

    private static long GetTotalBytesSent()
    {
        long total = 0;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                total += ni.GetIPStatistics().BytesSent;
            }
            catch {}
        }

        return total;
    }

    private static string? GetFirstMatchingProcessVersion(string containsName)
    {
        try
        {
            var proc = Process.GetProcesses().FirstOrDefault(p => p.ProcessName.Contains(containsName, StringComparison.OrdinalIgnoreCase));

            if (proc is null)
                return null;

            return proc.MainModule?.FileVersionInfo?.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}