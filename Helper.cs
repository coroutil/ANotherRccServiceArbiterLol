using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
        protocol = protocol.ToUpperInvariant();

        for (var i = 0; i < 1000; i++)
        {
            int port;

            if (protocol == "TCP")
            {
                while (true)
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    try
                    {
                        listener.Start();
                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("SocketException TCP");
                    }
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    if (port >= minimumPort && port <= maximumPort)
                    {
                        listener.Stop();
                        return port;
                    }
                }
            }
            else if (protocol == "UDP")
            {
                while (true)
                {
                    using var udp = new UdpClient(0);
                    port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                    if (port >= minimumPort && port <= maximumPort)
                    {
                        udp.Dispose();
                        return port;
                    }
                }
            }
            else
            {
                throw new ArgumentException("Protocol must be TCP or UDP.");
            }
        }

        throw new Exception($"Unable to obtain a {protocol} port in range {minimumPort}-{maximumPort}.");
    }

    public static List<LuaValue> ParseArguments(string? input)
    {
        var list = new List<LuaValue>();

        if (string.IsNullOrWhiteSpace(input))
            return list;

        foreach (var value in input.Split(','))
        {
            var arg = value.Trim();

            if (bool.TryParse(arg, out var b))
                list.Add(LuaValue.FromBool(b));
            else if (double.TryParse(arg, out var n))
                list.Add(LuaValue.FromNumber(n));
            else
                list.Add(LuaValue.FromString(arg));
        }

        return list;
    }

    public static bool fixitup(string input, out string output)
    {
        output = input.Trim().Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace('-', '+').Replace('_', '/');
        int mod = output.Length % 4;
        if (mod == 2) output += "==";
        else if (mod == 3) output += "=";
        else if (mod == 1) return false;

        try
        {
            Convert.FromBase64String(output);
            return true;
        }
        catch
        {
            return false;
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