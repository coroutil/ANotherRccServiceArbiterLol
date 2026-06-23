using System.Collections.Concurrent;
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

    private static async Task WaitForReady(RCCService rcc)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            if (!rcc.IsAlive)
                throw new Exception("An unexpected error occured during RCCService startup.");

            try
            {
                using var client = new TcpClient();

                await client.ConnectAsync(IPAddress.Loopback, rcc.Port);

                return;
            }
            catch
            {
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException();
    }

    private static void SpawnRCCService()
    {
        var port = Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntRCCServiceMinPort"), Configuration.GetIntFlag("DFIntRCCServiceMaxPort"), "TCP");
        var rcc = RCCService.Start(port);

        RegisterProcess(rcc);

        Console.WriteLine($"Started RCCService Instance pid={rcc.Process.Id}");

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
        Console.WriteLine($"TargetPoolSize={TargetPoolSize}");

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

    public static void Kill(RCCService rcc) // A once wise band said: KILL ALL THE FAGS THAT DON'T AGREE!
    {
        RemoveRCCService(rcc.Port);
    }

    public static void Kill(GameMonitorService.GMSJob job)
    {
        RemoveRCCService(job.Port);
    }

    private static void RemoveRCCService(int port)
    {
        Active.TryRemove(port, out _);
        Idle.TryRemove(port, out _);
        Pending.TryRemove(port, out _);
        ArbiterProcessIds.TryRemove(port, out _);
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

                Console.WriteLine($"Pending={Pending.Count}, Idle={Idle.Count}, Active={Active.Count}, Target={TargetPoolSize}");
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
    }
}