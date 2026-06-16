using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

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
    public ReverseProxy(int listenPort, int targetPort)
    {
        ListenPort = listenPort;
        TargetPort = targetPort;

        _listener = new UdpClient(AddressFamily.InterNetworkV6);
        _listener.Client.DualMode = true;
        _listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenPort));

        _target = new IPEndPoint(IPAddress.Loopback, targetPort);
    }

    public void Start()
    {
        if (_running)
            return;

        _running = true;

        Instances[ListenPort] = this;

        _ = Task.Run(RunAsync);
    }

    public void Stop()
    {
        _running = false;

        Instances.TryRemove(ListenPort, out _);

        try
        {
            _listener.Dispose();
        }
        catch
        {
        }

        foreach (var socket in _clients.Values)
        {
            try
            {
                socket.Dispose();
            }
            catch
            {
            }
        }

        _clients.Clear();
    }

    public static bool Stop(int listenPort)
    {
        if (!Instances.TryGetValue(listenPort, out var proxy))
        {
            return false;
        }

        proxy.Stop();

        return true;
    }

    private async Task RunAsync()
    {
        while (_running)
        {
            UdpReceiveResult result;

            try
            {
                result =
                    await _listener.ReceiveAsync();
            }
            catch
            {
                if (!_running)
                    break;

                continue;
            }

            var client = result.RemoteEndPoint;

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

    private async Task HandleServerTraffic(IPEndPoint client, UdpClient serverSocket)
    {
        while (_running)
        {
            try
            {
                var result =
                    await serverSocket.ReceiveAsync();

                await _listener.SendAsync(result.Buffer, result.Buffer.Length, client);
            }
            catch
            {
                break;
            }
        }

        serverSocket.Dispose();

        _clients.TryRemove(client, out _);
    }
}