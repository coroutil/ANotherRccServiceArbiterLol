using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Xml;

namespace Arbiter;

public sealed class ReverseProxy
{
    private static readonly ConcurrentDictionary<int, ReverseProxy> Instances = new();
    private readonly UdpClient _listener;
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<IPEndPoint, UdpClient> _clients = new();
    private bool _running;
    public int ListenPort { get; }
    public int TargetPort { get; }

    private const int MaxUdpPayload = 8192;
    private const long ClientMaxBytesPerSec = 8 * 1024 * 1024;
    private const int ClientMaxPacketsPerSec = 400;

    private const long GlobalMaxBytesPerSec = 50 * 1024 * 1024;
    private const int GlobalMaxPacketsPerSec = 2500;

    private sealed class RateState
    {
        public long Bytes;
        public int Packets;
        public long WindowStartTicks;
    }

    private readonly ConcurrentDictionary<IPEndPoint, RateState> _clientRates = new();
    private readonly RateState _globalRate = new();
    private readonly object _globalRateLock = new();
    private readonly ConcurrentDictionary<IPEndPoint, byte> _trustedClients = new();

    private static long NowTicks() => Stopwatch.GetTimestamp();
    private static long TicksPerSecond() => Stopwatch.Frequency;

    public ReverseProxy(int listenPort, int targetPort)
    {
        ListenPort = listenPort;
        TargetPort = targetPort;

        _listener = new UdpClient(ListenPort);
        _target = new IPEndPoint(IPAddress.Loopback, TargetPort);
    }

    private bool AllowClientRate(IPEndPoint client, int packetLen)
    {
        var now = NowTicks();
        var secTicks = TicksPerSecond();

        var state = _clientRates.GetOrAdd(client, _ => new RateState { WindowStartTicks = now });

        lock (state)
        {
            if (now - state.WindowStartTicks >= secTicks)
            {
                state.WindowStartTicks = now;
                state.Bytes = 0;
                state.Packets = 0;
            }

            if (state.Packets >= ClientMaxPacketsPerSec) return false;
            if (state.Bytes + packetLen > ClientMaxBytesPerSec) return false;

            state.Packets++;
            state.Bytes += packetLen;
            return true;
        }
    }

    private bool AllowGlobalRate(int packetLen)
    {
        var now = NowTicks();
        var secTicks = TicksPerSecond();

        lock (_globalRateLock)
        {
            if (now - _globalRate.WindowStartTicks >= secTicks)
            {
                _globalRate.WindowStartTicks = now;
                _globalRate.Bytes = 0;
                _globalRate.Packets = 0;
            }

            if (_globalRate.Packets >= GlobalMaxPacketsPerSec) return false;
            if (_globalRate.Bytes + packetLen > GlobalMaxBytesPerSec) return false;

            _globalRate.Packets++;
            _globalRate.Bytes += packetLen;
            return true;
        }
    }

    private static int FindMagicIndex(byte[] buf)
    {
        if (buf == null || buf.Length < 5) return -1;

        ReadOnlySpan<byte> magic = stackalloc byte[] { 0x00, 0xFF, 0xFF, 0x00, 0xFE };

        int maxSearch = Math.Min(buf.Length - magic.Length, 256);
        for (int i = 0; i <= maxSearch; i++)
        {
            bool match = true;
            for (int j = 0; j < magic.Length; j++)
            {
                if (buf[i + j] != magic[j]) { match = false; break; }
            }
            if (match) return i;
        }

        return -1;
    }

    private static bool IsRakNetHandshake(byte[] buf, out int magicIndex)
    {
        magicIndex = FindMagicIndex(buf);
        return magicIndex >= 0;
    }


    public void Start() {
        if (_running)
            return;

        _running = true;
        Instances[ListenPort] = this;

        _ = Task.Run(RunAsync);
    }

    public void Stop() {
        _running = false;

        Instances.TryRemove(ListenPort, out _);

        try { _listener.Dispose(); } catch { }

        foreach (var socket in _clients.Values)
        {
            try { socket.Dispose(); } catch { }
        }

        _clients.Clear();
    }

    public static bool Stop(int listenPort) {
        if (!Instances.TryGetValue(listenPort, out var proxy))
            return false;

        proxy.Stop();
        return true;
    }

    private bool ShouldAllowClient(IPEndPoint client, byte[] datagram)
    {
        if (_trustedClients.ContainsKey(client))
            return true;

        if (!IsRakNetHandshake(datagram, out var idx))
        {
            Console.WriteLine($"{client}: no magic");
            return false;
        }

        Console.WriteLine($"{client}: magic at offset {idx}");

        _trustedClients.TryAdd(client, 0);
        return true;
    }


    private async Task RunAsync() {
        while (_running)
        {
            UdpReceiveResult result;

            try
            {
                result = await _listener.ReceiveAsync();
            }
            catch
            {
                if (!_running) break;
                continue;
            }

            var client = result.RemoteEndPoint;
            var buffer = result.Buffer;

            if (buffer.Length == 0) continue;
            if (buffer.Length > MaxUdpPayload)
            {
                Console.WriteLine($"DROP too big: len={buffer.Length} from={client}");
                continue;
            }

            if (!AllowGlobalRate(buffer.Length))
                continue;

            if (!AllowClientRate(client, buffer.Length))
                continue;

            if (!ShouldAllowClient(client, buffer))
                continue;

            if (!_clients.TryGetValue(client, out var server))
            {
                server = new UdpClient(0);
                _clients[client] = server;
                _ = Task.Run(() => HandleServerTraffic(client, server));
            }

            try
            {
                await server.SendAsync(result.Buffer, result.Buffer.Length, _target);
            }
            catch
            {
            }
        }
    }

    private async Task HandleServerTraffic(IPEndPoint client, UdpClient serverSocket) {
        while (_running)
        {
            try
            {
                var result = await serverSocket.ReceiveAsync();
                await _listener.SendAsync(result.Buffer, result.Buffer.Length, client);
            }
            catch
            {
                break;
            }
        }

        serverSocket.Dispose();
        _clients.TryRemove(client, out _);
        _trustedClients.TryRemove(client, out _);
    }
}
