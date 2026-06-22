using System.Collections.Concurrent;
using static Arbiter.GameMonitorService;

namespace Arbiter;

public static class GameMonitorService
{
    private static readonly ConcurrentDictionary<string, GMSJob> _jobs = new();

    public static bool Insert(GMSJob job)
    {
        return _jobs.TryAdd(job.JobId, job);
    }

    public static GMSJob? Get(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    public static bool Remove(string jobId)
    {
        return _jobs.TryRemove(jobId, out _);
    }

    public static bool Exists(string jobId)
    {
        return _jobs.ContainsKey(jobId);
    }

    public static GMSJob? GetByPID(int pid)
    {
        return _jobs.Values.FirstOrDefault(x => x.Pid == pid);
    }

    public static GMSJob? GetByPort(int port)
    {
        return _jobs.Values.FirstOrDefault(x => x.SOAP == port);
    }

    public static IReadOnlyCollection<GMSJob> GetAll()
    {
        return _jobs.Values.ToList().AsReadOnly();
    }

    public record GMSJob
    {
        public string JobId { get; init; } = "";
        public int Port { get; init; }
        public int SOAP { get; init; }
        public long PlaceId { get; init; }
        public int Pid { get; set; }
    }
}