using System.Diagnostics;

namespace Arbiter;

public sealed class RCCService
{
    public Process Process { get; }
    public int Port { get; }
    public RCCService(Process process, int port)
    {
        Process = process;
        Port = port;
    }
    public bool IsAlive => !Process.HasExited;
    public void Kill()
    {
        try
        {
            Process.Kill(true);
        }
        catch (Exception e) { 
            // genuinely what the fuck happend
            throw new Exception(e.Message);
        }
    }

    public static RCCService Start(int port)
    {
        var path = Configuration.GetStringFlag("FStringRCCServicePath");
        var name = Configuration.GetStringFlag("FStringRCCServiceName");

        if (string.IsNullOrWhiteSpace(name))
            name = "RCCService";

        var exe = Path.Combine(path, $"{name}.exe");

        if (!File.Exists(exe))
            throw new FileNotFoundException(exe);

        string arguments;

        if (Configuration.GetFlag("FFlagRCCServiceOnlySpeaksJSON"))
        {
            arguments =
                Configuration.GetFlag("FFlagDebug")
                    ? $"-verbose -settingsfile \"DevSettingsFile.json\" -Console -port {port}"
                    : Configuration.GetFlag("FFlagVerbose")
                        ? $"-verbose -Console -port {port}"
                        : $"-Console -port {port}";
        }
        else
        {
            arguments = $"/Console /content:content\\\\ {port}";
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = path,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized
        })!;

        process.PriorityClass = ProcessPriorityClass.High;

        return new RCCService(process, port);
    }
}