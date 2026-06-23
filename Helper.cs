using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
}