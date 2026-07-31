using Microsoft.Win32;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Arbiter.GameMonitorService;

namespace Arbiter;

public static class Helper
{
    public static bool IsPortAvailable(int port, string protocol)
    {
        protocol = protocol.ToUpperInvariant();

        var properties = IPGlobalProperties.GetIPGlobalProperties();

        if (protocol == "TCP")
        {
            return !properties
                .GetActiveTcpListeners()
                .Any(x => x.Port == port);
        }

        if (protocol == "UDP")
        {
            return !properties
                .GetActiveUdpListeners()
                .Any(x => x.Port == port);
        }

        throw new ArgumentException("Protocol must be TCP or UDP.");
    }

    public static int GetGameServerPort()
    {
        using var udp = new UdpClient(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    public static int GetAvailablePort(int minimumPort, int maximumPort, string protocol)
    {
        if (minimumPort > maximumPort)
            throw new ArgumentException("Invalid port range.");

        protocol = protocol.ToUpperInvariant();

        int count = maximumPort - minimumPort + 1;
        int start = Random.Shared.Next(count);

        for (int i = 0; i < count; i++)
        {
            int port = minimumPort + ((start + i) % count);

            try
            {
                if (protocol == "TCP")
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    return port;
                }

                if (protocol == "UDP")
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    return port;
                }

                throw new ArgumentException("Protocol must be TCP or UDP.");
            }
            catch (SocketException) {}
        }

        throw new Exception($"Unable to obtain a {protocol} port in range {minimumPort}-{maximumPort}.");
    }

    public static List<LuaValue> ParseArguments(List<object>? input)
    {
        var list = new List<LuaValue>();

        if (input == null)
            return list;

        foreach (var value in input)
        {
            switch (value)
            {
                case bool b:
                    list.Add(LuaValue.FromBool(b));
                    break;

                case string s:
                    list.Add(LuaValue.FromString(s));
                    break;

                case JsonElement je when je.ValueKind == JsonValueKind.Number:
                    list.Add(LuaValue.FromNumber(je.GetDouble()));
                    break;

                case JsonElement je when je.ValueKind == JsonValueKind.String:
                    list.Add(LuaValue.FromString(je.GetString()!));
                    break;

                case int i:
                case long l:
                case double d:
                case float f:
                    list.Add(LuaValue.FromNumber(Convert.ToDouble(value)));
                    break;

                default:
                    list.Add(LuaValue.FromString(value?.ToString() ?? ""));
                    break;
            }
        }

        return list;
    }

    public static string fixitup(string input)//, out string output)
    {
        string output = "";
        output = input.Trim().Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace('-', '+').Replace('_', '/');
        int mod = output.Length % 4;
        if (mod == 2) output += "==";
        else if (mod == 3) output += "=";
        else if (mod == 1) return "diddy";

        try
        {
            Convert.FromBase64String(output);
            return output;
        }
        catch
        {
            return "diddy";
        }
    }

    public static string ProcessArguments(string script, List<LuaValue> args)
    {
        if (string.IsNullOrEmpty(script) || args == null)
            return script;

        var result = new StringBuilder(script.Length);

        for (int pos = 0; pos < script.Length; pos++)
        {
            if (script[pos] == '(')
            {
                int end = script.IndexOf(')', pos);

                if (end > pos + 1)
                {
                    string token = script.Substring(pos + 1, end - pos - 1);

                    if (int.TryParse(token, out int index))
                    {
                        index--; // (1) => args[0]

                        if (index >= 0 && index < args.Count)
                        {
                            result.Append(args[index]?.ToString() ?? "null");
                            pos = end;
                            continue;
                        }
                    }
                }
            }

            result.Append(script[pos]);
        }

        return result.ToString();
    }

    public static string GetNodeId()
    {
        var machineId = GetMachineId();

        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(machineId));

        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);

        return new Guid(guidBytes).ToString();
    }

    public static string GetMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null);

                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            else if (File.Exists("/etc/machine-id"))
            {
                var s = File.ReadAllText("/etc/machine-id").Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        catch
        {
        }

        return Environment.MachineName;
    }

    public static ulong GetSuitableAffinity()
    {
        int cores = Environment.ProcessorCount;

        ulong mask = 0;

        for (int i = 1; i < cores; i++)
            mask |= 1UL << i;

        return mask;
    }

    public static bool ValidatePacket(ReadOnlySpan<byte> packet)
    {
        // need at least packet id + rpc id + compressed uint32
        if (packet.Length < 3)
            return false;

        byte packetId = packet[0];

        // PAWN: packetid == 40
        if (packetId == 40)
            return true;

        // only RPC packets contain NumberOfBitsOfData
        // normal RakNet RPC packet id
        if (packetId != 0x83)
            return false;

        int offset = 1;

        if (offset >= packet.Length)
            return true;

        byte rpcId = packet[offset++];

        if (!TryReadCompressedUInt32(packet, ref offset, out uint numberOfBits))
            return true;

        // equivalent sanity checks
        if (numberOfBits >= 0x1FFFFFu)
        {
            Logger.Warning($"RPC {rpcId}, NumberOfBitsOfData={numberOfBits}");
            return true;
        }

        return false;
    }

    private static bool TryReadCompressedUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        int shift = 0;

        while (offset < data.Length && shift < 35)
        {
            byte b = data[offset++];
            value |= (uint)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return true;

            shift += 7;
        }

        return false;
    }

    public static void ApplyMitigations(Process process)
    {
        try
        {
            // DEP
            SetMitigation(
                process.Handle,
                PROCESS_MITIGATION_POLICY.ProcessDEPPolicy,
                new PROCESS_MITIGATION_DEP_POLICY
                {
                    Enable = 1,
                    DisableAtlThunkEmulation = 1
                });

            // ASLR
            SetMitigation(
                process.Handle,
                PROCESS_MITIGATION_POLICY.ProcessASLRPolicy,
                new PROCESS_MITIGATION_ASLR_POLICY
                {
                    EnableBottomUpRandomization = 1,
                    EnableForceRelocateImages = 1
                });

            // Strict handle checks
            SetMitigation(
                process.Handle,
                PROCESS_MITIGATION_POLICY.ProcessStrictHandleCheckPolicy,
                new PROCESS_MITIGATION_STRICT_HANDLE_POLICY
                {
                    RaiseExceptionOnInvalidHandleReference = 1
                });
        }
        catch (Exception ex)
        {
            Logger.Warning($"Mitigation setup failed: {ex.Message}");
        }
    }

    private static void SetMitigation<T>(IntPtr process, PROCESS_MITIGATION_POLICY policy, T value) where T : struct {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(value, ptr, false);

            if (!SetProcessMitigationPolicy(policy, ptr, (UIntPtr)size))
            {
                throw new InvalidOperationException($"SetProcessMitigationPolicy failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(PROCESS_MITIGATION_POLICY mitigationPolicy, IntPtr lpBuffer, UIntPtr dwLength);
    private enum PROCESS_MITIGATION_POLICY
    {
        ProcessDEPPolicy = 0,
        ProcessASLRPolicy = 1,
        ProcessStrictHandleCheckPolicy = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MITIGATION_DEP_POLICY
    {
        public uint Enable;
        public uint DisableAtlThunkEmulation;
        public uint ReservedFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MITIGATION_ASLR_POLICY
    {
        public uint EnableBottomUpRandomization;
        public uint EnableForceRelocateImages;
        public uint EnableHighEntropy;
        public uint DisallowStrippedImages;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MITIGATION_STRICT_HANDLE_POLICY
    {
        public uint RaiseExceptionOnInvalidHandleReference;
    }

    public static void DisablePowerThrottling(Process process)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0 // disable throttling
            };

            if (!SetProcessInformation(process.Handle, PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
            {
                throw new InvalidOperationException($"SetProcessInformation failed: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Power throttling disable failed: {ex.Message}");
        }
    }

    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private enum PROCESS_INFORMATION_CLASS
    {
        ProcessPowerThrottling = 4
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        PROCESS_INFORMATION_CLASS processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize);
}