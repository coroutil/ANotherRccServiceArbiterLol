using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;
using System.Security;

namespace Arbiter;

public sealed class RCCService
{
    private static readonly Sandbox Sandbox =
        SandboxManager.Create()
            .SetProcessMemoryLimit(2UL << 30)
            .SetJobMemoryLimit(6UL << 30)
            .SetActiveProcessLimit(32)
            .SetAffinity(Helper.GetSuitableAffinity())
            .SetCPUHardCap(80) // dont wanna brick the vps do we
            .SetPriorityClass(ProcessPriorityClass.AboveNormal); // bro what teh fuck
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


        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = path,
        };

        if (WindowsServiceHelpers.IsWindowsService())
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            startInfo.UseShellExecute = true;
            startInfo.CreateNoWindow = false;
            startInfo.WindowStyle = ProcessWindowStyle.Minimized;
        }

        var process = Process.Start(startInfo)!;

        process.PriorityClass = ProcessPriorityClass.AboveNormal;
        Helper.ApplyMitigations(process);
        Helper.DisablePowerThrottling(process);

        Sandbox.Add(process);

        Logger.Info($"RCCService instance started on port {port} with PID {process.Id}");

        return new RCCService(process, port);
    }
}