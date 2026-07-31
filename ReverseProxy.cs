using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Arbiter;

public sealed class ReverseProxy : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<int, ReverseProxy> Instances = new();

    private readonly Socket _listener;
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<IPEndPoint, ClientSession> _clients = new();
    private readonly ConcurrentDictionary<IPEndPoint, SemaphoreSlim> _clientGates = new();
    private readonly ConcurrentDictionary<IPAddress, byte> _ownedIps = new();
    private readonly ConcurrentDictionary<IPEndPoint, RateState> _clientRates = new();
    private readonly RateState _globalRate = new();
    private readonly object _globalRateLock = new();
    private readonly VirtualIpAllocator _ipAllocator = new();
    private readonly CancellationTokenSource _cts = new();

    private Task[] _receiveTasks = Array.Empty<Task>();
    private Task? _reaperTask;
    private Task[] _workers = Array.Empty<Task>();

    private int _running;
    private int _started;

    public int ListenPort { get; }
    public int TargetPort { get; }

    private const int MaxUdpPayload = 16384;
    private const int ClientMaxPacketsPerSec = 400;
    private const int GlobalMaxPacketsPerSec = 2500;
    private const long ClientMaxBytesPerSec = 8 * 1024 * 1024;
    private const long GlobalMaxBytesPerSec = 50 * 1024 * 1024;

    private const string VirtualInterfaceAlias = "Ethernet";
    private const int VirtualPrefixLength = 16;

    private static long NowTicks() => Stopwatch.GetTimestamp();

    private readonly Channel<Packet> _ingress;

    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReaperInterval = TimeSpan.FromSeconds(15);

    private readonly uint _interfaceIndex;

    private readonly record struct Packet(IPEndPoint Client, byte[] Buffer, int Length);

    private sealed class ClientSession
    {
        public readonly Socket Socket;
        public readonly IPAddress VirtualIP;

        public readonly Channel<(byte[] Buffer, int Length)> ToServer =
            Channel.CreateBounded<(byte[] Buffer, int Length)>(
                new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

        public int Started;
        public int Disposed;
        public int ActiveLoops = 2;
        public long LastSeenTicks;

        public ClientSession(IPAddress virtualIP)
        {
            VirtualIP = virtualIP;
            LastSeenTicks = NowTicks();

            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 4 * 1024 * 1024,
                SendBufferSize = 4 * 1024 * 1024
            };

            try
            {
                Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8);

                const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                Socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch (SocketException ex)
            {
                Logger.Warning($"Socket QoS setup failed: {ex.Message}");
            }

            Socket.Bind(new IPEndPoint(virtualIP, 0));
        }
    }

    private sealed class VirtualIpAllocator
    {
        private readonly ConcurrentDictionary<IPEndPoint, IPAddress> _map = new();
        private int _next = 10;

        public IPAddress Get(IPEndPoint client)
        {
            return _map.GetOrAdd(client, _ =>
            {
                int value = Interlocked.Increment(ref _next);

                if (value >= 65535)
                    throw new Exception("Virtual IP pool exhausted");

                int thirdOctet = value / 253;
                int fourthOctet = (value % 253) + 1;

                return IPAddress.Parse($"172.31.{thirdOctet}.{fourthOctet}");
            });
        }

        public void Release(IPEndPoint client)
        {
            _map.TryRemove(client, out _);
        }
    }

    private sealed class RateState
    {
        public long Bytes;
        public int Packets;
        public long WindowStartTicks;
    }

    public ReverseProxy(int listenPort, int targetPort, int workerCount = 0)
    {
        ListenPort = listenPort;
        TargetPort = targetPort;

        _interfaceIndex = ResolveInterfaceIndex(VirtualInterfaceAlias);

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 32 * 1024 * 1024,
            SendBufferSize = 32 * 1024 * 1024
        };
        try
        {
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8);

            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            _listener.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch (SocketException ex)
        {
            Logger.Warning($"Socket QoS setup failed: {ex.Message}");
        }
        _listener.Bind(new IPEndPoint(IPAddress.Any, ListenPort));

        _target = new IPEndPoint(GetPrivateIPv4(), TargetPort);

        _ingress = Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(32768)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = false
            });

        if (workerCount <= 0)
            workerCount = Math.Max(2, Environment.ProcessorCount);

        _workers = new Task[workerCount];

        _receiveTasks = new Task[Math.Max(2, Environment.ProcessorCount / 2)];

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        NativeMethods.timeBeginPeriod(1);
    }

    private static string PsQuote(string value) => value.Replace("'", "''");

    private static uint ResolveInterfaceIndex(string alias)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (!string.Equals(ni.Name, alias, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ni.Description, alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ipv4 = ni.GetIPProperties().GetIPv4Properties();
            if (ipv4 is not null && ipv4.Index > 0)
                return (uint)ipv4.Index;
        }

        throw new Exception($"Could not resolve IPv4 interface index for '{alias}'.");
    }

    private static IPAddress GetPrivateIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (IPAddress.IsLoopback(addr.Address))
                    continue;

                return addr.Address;
            }
        }

        throw new Exception("No private IPv4 found");
    }

    private static bool IsManagedVirtualIp(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 172 && bytes[1] == 31;
    }

    private static MIB_UNICASTIPADDRESS_ROW CreateRow(uint interfaceIndex, IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 is supported.", nameof(ip));

        var row = new MIB_UNICASTIPADDRESS_ROW();
        NativeMethods.InitializeUnicastIpAddressEntry(ref row);

        row.InterfaceIndex = interfaceIndex;
        row.Address = SOCKADDR_INET.FromIPv4(ip);
        row.OnLinkPrefixLength = VirtualPrefixLength;
        row.SkipAsSource = 1;

        return row;
    }

    private static bool IsDuplicateOrExists(uint status)
    {
        return status == 0x00001389 /* ERROR_DUPLICATE_NAME */
            || status == 0x00001392 /* ERROR_OBJECT_ALREADY_EXISTS */
            || status == 0xC0000035 /* STATUS_OBJECT_NAME_COLLISION */
            || status == 0x00000000;
    }

    private bool AllowClientRate(IPEndPoint client, int length)
    {
        var now = NowTicks();

        var state = _clientRates.GetOrAdd(client, _ => new RateState
        {
            WindowStartTicks = now
        });

        lock (state)
        {
            if (now - state.WindowStartTicks >= Stopwatch.Frequency)
            {
                state.WindowStartTicks = now;
                state.Bytes = 0;
                state.Packets = 0;
            }

            if (state.Packets >= ClientMaxPacketsPerSec)
                return false;

            if (state.Bytes + length > ClientMaxBytesPerSec)
                return false;

            state.Packets++;
            state.Bytes += length;
            return true;
        }
    }

    private bool AllowGlobalRate(int length)
    {
        var now = NowTicks();

        lock (_globalRateLock)
        {
            if (now - _globalRate.WindowStartTicks >= Stopwatch.Frequency)
            {
                _globalRate.WindowStartTicks = now;
                _globalRate.Bytes = 0;
                _globalRate.Packets = 0;
            }

            if (_globalRate.Packets >= GlobalMaxPacketsPerSec)
                return false;

            if (_globalRate.Bytes + length > GlobalMaxBytesPerSec)
                return false;

            _globalRate.Packets++;
            _globalRate.Bytes += length;

            return true;
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        ClearStaleManagedIpsOnInterface();

        Volatile.Write(ref _running, 1);
        Instances[ListenPort] = this;

        for (int i = 0; i < _receiveTasks.Length; i++)
        {
            _receiveTasks[i] = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        _reaperTask = Task.Run(() => ReaperLoopAsync(_cts.Token));

        for (int i = 0; i < _workers.Length; i++)
            _workers[i] = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        Instances.TryRemove(ListenPort, out _);

        _cts.Cancel();
        _ingress.Writer.TryComplete();

        try { _listener.Dispose(); } catch { }

        try
        {
            await Task.WhenAll(_receiveTasks);
        }
        catch { }

        try
        {
            if (_reaperTask is not null)
                await _reaperTask.ConfigureAwait(false);
        }
        catch { }

        try
        {
            await Task.WhenAll(_workers.Where(t => t is not null)).ConfigureAwait(false);
        }
        catch { }

        foreach (var pair in _clients.ToArray())
            await DisposeSessionAsync(pair.Key, pair.Value).ConfigureAwait(false);

        _clients.Clear();

        foreach (var ip in _ownedIps.Keys.ToArray())
            await TryDeleteVirtualIpAsync(ip).ConfigureAwait(false);

        _ownedIps.Clear();
        _clientRates.Clear();
        _cts.Dispose();
    }

    public static async Task<bool> StopAsync(int listenPort)
    {
        if (!Instances.TryGetValue(listenPort, out var proxy))
            return false;

        await proxy.StopAsync().ConfigureAwait(false);
        return true;
    }

    private void ClearStaleManagedIpsOnInterface()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var ipv4 = ni.GetIPProperties().GetIPv4Properties();
            if (ipv4 is null || (uint)ipv4.Index != _interfaceIndex)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (!IsManagedVirtualIp(addr.Address))
                    continue;

                try
                {
                    TryDeleteVirtualIpSync(addr.Address);
                    Logger.Debug($"Cleared stale virtual IP {addr.Address}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to clear stale virtual IP {addr.Address}: {ex}");
                }
            }
        }
    }

    private Task ReceiveLoopAsync(CancellationToken ct)
    {
        uint taskIndex = 0;
        IntPtr mmcss = NativeMethods.AvSetMmThreadCharacteristics("Games", out taskIndex);

        if (mmcss != IntPtr.Zero)
            NativeMethods.AvSetMmThreadPriority(mmcss, 1);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var args = new SocketAsyncEventArgs();

        args.SetBuffer(ArrayPool<byte>.Shared.Rent(MaxUdpPayload), 0, MaxUdpPayload);
        args.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        args.Completed += Completed;

        void Completed(object? sender, SocketAsyncEventArgs e)
        {
            try
            {
                ProcessReceive(e);

                if (!ct.IsCancellationRequested)
                    StartReceive(e);
            }
            catch (Exception ex)
            {
                Logger.Error($"UDP receive failed: {ex}");
                tcs.TrySetException(ex);
            }
        }


        void ProcessReceive(SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success)
                return;

            int length = e.BytesTransferred;

            if ((uint)length > MaxUdpPayload || length == 0)
                return;


            if (!AllowGlobalRate(length))
                return;


            // transfer ownership
            byte[] packetBuffer = e.Buffer!;


            var packet = new Packet(
                (IPEndPoint)e.RemoteEndPoint!,
                packetBuffer,
                length);


            if (_ingress.Writer.TryWrite(packet))
            {
                // worker owns packetBuffer now
                e.SetBuffer(
                    ArrayPool<byte>.Shared.Rent(MaxUdpPayload),
                    0,
                    MaxUdpPayload);
            }
            else
            {
                // queue rejected, keep using buffer
                e.SetBuffer(
                    packetBuffer,
                    0,
                    MaxUdpPayload);
            }
        }


        void StartReceive(SocketAsyncEventArgs e)
        {
            e.RemoteEndPoint = new IPEndPoint(
                IPAddress.Any,
                0);


            if (!_listener.ReceiveFromAsync(e))
            {
                ProcessReceive(e);

                if (!ct.IsCancellationRequested)
                    StartReceive(e);
            }
        }


        StartReceive(args);


        ct.Register(() =>
        {
            try
            {
                args.Completed -= Completed;
                args.Dispose();
            }
            catch { }

            tcs.TrySetResult();
        });


        return Cleanup();


        async Task Cleanup()
        {
            await tcs.Task.ConfigureAwait(false);

            if (args.Buffer != null)
                ArrayPool<byte>.Shared.Return(args.Buffer);

            if (mmcss != IntPtr.Zero)
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);

            args.Dispose();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        try
        {
            await foreach (var packet in _ingress.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                bool handedOff = false;

                try
                {
                    if (!AllowClientRate(packet.Client, packet.Length))
                        continue;

                    var session = await GetOrCreateSessionAsync(packet.Client).ConfigureAwait(false);

                    if (session is null)
                        continue;

                    Volatile.Write(ref session.LastSeenTicks, NowTicks());

                    if (session.ToServer.Writer.TryWrite((packet.Buffer, packet.Length)))
                    {
                        handedOff = true;
                    }
                }
                finally
                {
                    if (!handedOff)
                        ArrayPool<byte>.Shared.Return(packet.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"Worker loop failed: {ex}");
        }
    }

    private async Task ReaperLoopAsync(CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.Normal;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReaperInterval, ct).ConfigureAwait(false);

                var now = NowTicks();
                var timeoutTicks = (long)(SessionIdleTimeout.TotalSeconds * Stopwatch.Frequency);

                foreach (var pair in _clients.ToArray())
                {
                    var session = pair.Value;

                    if (Volatile.Read(ref session.Disposed) != 0)
                        continue;

                    long lastSeen = Volatile.Read(ref session.LastSeenTicks);
                    if (now - lastSeen < timeoutTicks)
                        continue;

                    Logger.Debug($"Reaping idle session {pair.Key} to {session.VirtualIP}");
                    await DisposeSessionAsync(pair.Key, session).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"Reaper loop failed: {ex}");
        }
    }

    private static bool IsAlive(ClientSession? session)
    {
        return session is not null && Volatile.Read(ref session.Disposed) == 0;
    }

    private async Task<ClientSession?> GetOrCreateSessionAsync(IPEndPoint client)
    {
        if (_clients.TryGetValue(client, out var existing) && IsAlive(existing))
            return existing;

        var gate = _clientGates.GetOrAdd(client, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_clients.TryGetValue(client, out existing) && IsAlive(existing))
                return existing;

            if (existing is not null && Volatile.Read(ref existing.Disposed) != 0)
                _clients.TryRemove(client, out _);

            var ip = _ipAllocator.Get(client);

            if (!await EnsureVirtualIpOnInterfaceAsync(ip).ConfigureAwait(false))
            {
                Logger.Critical($"Failed to assign virtual IP {ip} for {client}");
                _ipAllocator.Release(client);
                return null;
            }

            _ownedIps.TryAdd(ip, 0);

            ClientSession session;
            try
            {
                session = new ClientSession(ip);
            }
            catch
            {
                _ownedIps.TryRemove(ip, out _);
                _ipAllocator.Release(client);
                await TryDeleteVirtualIpAsync(ip).ConfigureAwait(false);
                throw;
            }

            _clients[client] = session;
            Logger.Debug($"Assigned {client} to {ip}");

            StartSession(client, session);
            return session;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to create session for {client}: {ex}");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private void StartSession(IPEndPoint client, ClientSession session)
    {
        if (Interlocked.Exchange(ref session.Started, 1) != 0)
            return;

        _ = Task.Run(() => RunSessionLoopAsync(client, session, ClientToServerLoopAsync, _cts.Token));
        _ = Task.Run(() => RunSessionLoopAsync(client, session, ServerToClientLoopAsync, _cts.Token));
    }

    private async Task RunSessionLoopAsync(IPEndPoint client, ClientSession session, Func<IPEndPoint, ClientSession, CancellationToken, Task> loop, CancellationToken ct)
    {
        try
        {
            await loop(client, session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"Session loop failed for {client} to {session.VirtualIP}: {ex}");
        }
        finally
        {
            if (Interlocked.Decrement(ref session.ActiveLoops) == 0)
                await DisposeSessionAsync(client, session).ConfigureAwait(false);
        }
    }

    private async Task ClientToServerLoopAsync(IPEndPoint client, ClientSession session, CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        await foreach (var packet in session.ToServer.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (Helper.ValidatePacket(packet.Buffer.AsSpan(0, packet.Length)))
                {
                    Logger.Warning($"Blocked packet from {client}");
                    await DisposeSessionAsync(client, session).ConfigureAwait(false);
                    continue;
                }
                await session.Socket.SendToAsync(packet.Buffer.AsMemory(0, packet.Length), SocketFlags.None, _target, ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
    }

    private async Task ServerToClientLoopAsync(IPEndPoint client, ClientSession session, CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        while (!ct.IsCancellationRequested)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxUdpPayload);

            try
            {
                int received = await session.Socket.ReceiveAsync(buffer.AsMemory(0, MaxUdpPayload), SocketFlags.None, ct).ConfigureAwait(false);

                if (received <= 0)
                    continue;

                Volatile.Write(ref session.LastSeenTicks, NowTicks());

                await _listener.SendToAsync(buffer.AsMemory(0, received), SocketFlags.None, client, ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private bool TryDeleteVirtualIpSync(IPAddress ip)
    {
        var row = CreateRow(_interfaceIndex, ip);
        uint status = NativeMethods.DeleteUnicastIpAddressEntry(ref row);
        return status == 0;
    }

    private Task<bool> TryDeleteVirtualIpAsync(IPAddress ip)
    {
        try
        {
            return Task.FromResult(TryDeleteVirtualIpSync(ip));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private Task<bool> TryCreateVirtualIpAsync(IPAddress ip)
    {
        try
        {
            var row = CreateRow(_interfaceIndex, ip);
            uint status = NativeMethods.CreateUnicastIpAddressEntry(ref row);
            return Task.FromResult(status == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private async Task<bool> EnsureVirtualIpOnInterfaceAsync(IPAddress ip)
    {
        // best-effort stale cleanup before creating a fresh entry
        await TryDeleteVirtualIpAsync(ip).ConfigureAwait(false);

        if (await TryCreateVirtualIpAsync(ip).ConfigureAwait(false))
            return true;

        // If creation failed because of stale state, try one more delete/create
        await TryDeleteVirtualIpAsync(ip).ConfigureAwait(false);
        return await TryCreateVirtualIpAsync(ip).ConfigureAwait(false);
    }

    private async Task DisposeSessionAsync(IPEndPoint client, ClientSession session)
    {
        if (Interlocked.Exchange(ref session.Disposed, 1) != 0)
            return;

        try { session.ToServer.Writer.TryComplete(); } catch { }
        try { session.Socket.Dispose(); } catch { }

        _ipAllocator.Release(client);
        _ownedIps.TryRemove(session.VirtualIP, out _);
        _clientRates.TryRemove(client, out _);
        _clientGates.TryRemove(client, out _);
        _clients.TryRemove(client, out _);

        await TryDeleteVirtualIpAsync(session.VirtualIP).ConfigureAwait(false);
        Logger.Debug($"Disposed session {client} to {session.VirtualIP}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private static class NativeMethods
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        public static extern void InitializeUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW Row);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        public static extern uint CreateUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW Row);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        public static extern uint DeleteUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW Row);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string task, out uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        [DllImport("avrt.dll")]
        public static extern bool AvSetMmThreadPriority(IntPtr taskHandle, int priority);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);
    }

    private enum NL_PREFIX_ORIGIN : int
    {
        Other = 0
    }

    private enum NL_SUFFIX_ORIGIN : int
    {
        Other = 0
    }

    private enum NL_DAD_STATE : int
    {
        Invalid = 0,
        Tentative = 1,
        Duplicate = 2,
        Deprecated = 3,
        Preferred = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKADDR_IN
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct SOCKADDR_INET
    {
        [FieldOffset(0)]
        public SOCKADDR_IN Ipv4;

        public static SOCKADDR_INET FromIPv4(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                throw new ArgumentException("IPv4 address required.", nameof(ip));

            return new SOCKADDR_INET
            {
                Ipv4 = new SOCKADDR_IN
                {
                    sin_family = (ushort)AddressFamily.InterNetwork,
                    sin_port = 0,
                    sin_addr = BinaryPrimitives.ReadUInt32BigEndian(bytes),
                    sin_zero = 0
                }
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UNICASTIPADDRESS_ROW
    {
        public SOCKADDR_INET Address;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public NL_PREFIX_ORIGIN PrefixOrigin;
        public NL_SUFFIX_ORIGIN SuffixOrigin;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public NL_DAD_STATE DadState;
        public uint ScopeId;
        public long CreationTimeStamp;
    }
}