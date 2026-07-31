using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Arbiter;

public static class RCCServicePool
{
    public static readonly ConcurrentDictionary<int, RCCService> Pending = new();
    public static readonly ConcurrentDictionary<int, RCCService> Idle = new();
    public static readonly ConcurrentDictionary<int, RCCService> Active = new();
    private static readonly ConcurrentDictionary<int, byte> ArbiterProcessIds = new();
    private static readonly int TargetPoolSize = Configuration.GetIntFlag("DFIntRCCServicePoolSize");

    public static void RegisterProcess(RCCService rcc)
    {
        ArbiterProcessIds.TryAdd(rcc.Process.Id, 0);
    }

    public static bool IsManaged(RCCService rcc)
    {
        return ArbiterProcessIds.ContainsKey(rcc.Process.Id);
    }

    private static void MoveToIdle(int port, RCCService rcc)
    {
        if (Pending.TryRemove(port, out _))
        {
            Idle.TryAdd(port, rcc);
        }
    }

    public static async Task WaitForReady(RCCService rcc)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            if (!rcc.IsAlive)
                throw new Exception("An unexpected error occured during RCCService startup.");

            try
            {
                await SOAP.Send(
                    port: rcc.Port,
                    action: "HelloWorld",
                    script: string.Empty
                );

                if (rcc.Process.HasExited)
                    throw new Exception($"RCCService exited with code {rcc.Process.ExitCode}");

                return;
            }
            catch
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException();
    }

    private static void SpawnRCCService()
    {
        var port = Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntRCCServiceMinPort"), Configuration.GetIntFlag("DFIntRCCServiceMaxPort"), "TCP");
        var rcc = RCCService.Start(port);

        RegisterProcess(rcc);

        Logger.Info($"RCCService Instance started with pid={rcc.Process.Id}");

        var added = !Pending.TryAdd(port, rcc);

        if (added)
        {
            ArbiterProcessIds.TryRemove(rcc.Process.Id, out _);
            rcc.Kill();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WaitForReady(rcc);

                MoveToIdle(port, rcc);
            }
            catch
            {
                Pending.TryRemove(port, out _);
                ArbiterProcessIds.TryRemove(rcc.Process.Id, out _);
                rcc.Kill();
            }
        });
    }

    public static async Task InitializePool()
    {
        Logger.Debug($"TargetPoolSize={TargetPoolSize}");

        for (var i = 0; i < TargetPoolSize; i++)
        {
            SpawnRCCService();
        }
    }

    public static RCCService? Acquire()
    {
        foreach (var pair in Idle)
        {
            if (Idle.TryRemove(pair.Key, out var rcc))
            {
                Active.TryAdd(pair.Key, rcc);
                return rcc;
            }
        }

        return null;
    }

    public static void Release(RCCService rcc)
    {
        if (Active.TryRemove(rcc.Port, out _))
            Idle.TryAdd(rcc.Port, rcc);
    }

    public static void Kill(RCCService rcc, int pid = 0) // A once wise band said: KILL ALL THE FAGS THAT DON'T AGREE!
    {
        RemoveRCCService(rcc.Port, rcc.Process.Id);
    }

    public static void Kill(GameMonitorService.GMSJob job, int pid = 0)
    {
        RemoveRCCService(job.Port, job.Pid);
    }

    private static void RemoveRCCService(int port, int pid = 0)
    {
        if (Active.TryRemove(port, out var active))
            ArbiterProcessIds.TryRemove(active.Process.Id, out _);

        if (Idle.TryRemove(port, out var idle))
            ArbiterProcessIds.TryRemove(idle.Process.Id, out _);

        if (Pending.TryRemove(port, out var pending))
            ArbiterProcessIds.TryRemove(pending.Process.Id, out _);

        if (pid != 0)
            ArbiterProcessIds.TryRemove(pid, out _);
    }

    private static void CleanupDeadServices()
    {
        foreach (var pair in Pending)
        {
            var rcc = pair.Value;

            if (!IsManaged(rcc))
                continue;

            if (rcc.IsAlive)
                continue;

            Pending.TryRemove(pair.Key, out _);

            ArbiterProcessIds.TryRemove(rcc.Process.Id, out _);
        }

        foreach (var pair in Idle)
        {
            var rcc = pair.Value;

            if (!IsManaged(rcc))
                continue;

            if (rcc.IsAlive)
                continue;

            Idle.TryRemove(pair.Key, out _);

            ArbiterProcessIds.TryRemove(rcc.Process.Id, out _);
        }
    }

    private static int _maintenanceStarted = 0;

    public static async Task StartPoolMaintenance()
    {
        if (Interlocked.Exchange(ref _maintenanceStarted, 1) == 1)
            return;

        while (true)
        {
            try
            {
                CleanupDeadServices();

                var count = Pending.Count + Idle.Count + Active.Count;

                if (count < TargetPoolSize)
                {
                    var missing = TargetPoolSize - count;

                    for (var i = 0; i < missing; i++)
                        SpawnRCCService();
                }
            }
            catch
            {
            }

            await Task.Delay(1000);
        }
    }

    public static void Shutdown()
    {
        foreach (var pair in Pending)
        {
            try
            {
                pair.Value.Kill();
            }
            catch
            {
            }
        }

        foreach (var pair in Idle)
        {
            try
            {
                pair.Value.Kill();
            }
            catch
            {
            }
        }

        foreach (var pair in Active)
        {
            try
            {
                pair.Value.Kill();
            }
            catch
            {
            }
        }

        Pending.Clear();
        Idle.Clear();
        Active.Clear();
        ArbiterProcessIds.Clear();

        SandboxManager.DisposeEverything();
    }
}