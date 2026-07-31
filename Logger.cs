using Microsoft.Extensions.Logging;

namespace Arbiter;

public static class Logger
{
    private static ILogger? _logger;

    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    public static void Trace(string message)
    {
        _logger?.LogTrace(message);
    }

    public static void Debug(string message)
    {
        _logger?.LogDebug(message);
    }

    public static void Info(string message)
    {
        _logger?.LogInformation(message);
    }

    public static void Warning(string message)
    {
        _logger?.LogWarning(message);
    }

    public static void Error(string message)
    {
        _logger?.LogError(message);
    }

    public static void Critical(string message)
    {
        _logger?.LogCritical(message);
    }
}