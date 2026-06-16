using Microsoft.Extensions.Configuration;

namespace Arbiter;

public static class Configuration
{
    private static IConfiguration? _configuration;

    public static void Initialize(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    private static IConfigurationSection FastFlags
    {
        get
        {
            if (_configuration == null)
                throw new InvalidOperationException("Configuration not initialized.");

            return _configuration.GetSection("FastFlags");
        }
    }

    // if (Configuration.GetBool("DFFlagCantGetMad2")) { dynamic
    // if (Configuration.GetBool("FFlagDebugCrashEnabled")) { static
    // var cycleCount = Configuration.GetInt("DFIntAniCycleCount");
    // var blacklistHeaders = Configuration.GetString("DFStringAdditionalBlacklistHeaders");

    public static bool GetBool(string name, bool defaultValue = false)
    {
        return FastFlags.GetValue(name, defaultValue);
    }

    public static int GetInt(string name, int defaultValue = 0)
    {
        return FastFlags.GetValue(name, defaultValue);
    }

    public static string GetString(string name, string defaultValue = "")
    {
        return FastFlags.GetValue(name, defaultValue) ?? defaultValue;
    }

    // below usage:
    //  Configuration.GetFlag("DFFlagCantGetMad2");
    //  Configuration.GetIntFlag("DFIntAniCycleCount");
    //  Configuration.GetStringFlag("DFStringAdditionalBlacklistHeaders");


    public static bool GetFlag(string name)
    {
        return GetBool(name);
    }

    public static int GetIntFlag(string name)
    {
        return GetInt(name);
    }

    public static string GetStringFlag(string name)
    {
        return GetString(name);
    }
}