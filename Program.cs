/*
 * Secure your own servers.
 *
 * This arbiter does not include rate limiting or production security features.
 * If you deploy it, you are expected to handle those concerns yourself.
 *
 * If you don't secure it properly and it gets overloaded, that's on you.
 *
 * Use a firewall. Use HTTPS. It is not difficult.
 *
 * - unconnected
 */

using Arbiter;
using Arbiter.Middleware;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

var definitelynothardcodedargs = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};

var builder = WebApplication.CreateBuilder(definitelynothardcodedargs);

Configuration.Initialize(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var port = Configuration.GetIntFlag("FIntWebserverPort");

var certPath = Path.Combine(AppContext.BaseDirectory, "cert.crt");
var keyPath = Path.Combine(AppContext.BaseDirectory, "cert.key");
var httpsEnabled = System.IO.File.Exists(certPath) && System.IO.File.Exists(keyPath);

builder.WebHost.ConfigureKestrel(options =>
{
    if (httpsEnabled)
    {
        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        options.ListenAnyIP(port, listen => listen.UseHttps(cert));
        Logger.Info("HTTPS enabled");
    }
    else
    {
        options.ListenAnyIP(port);
        Logger.Info("HTTPS disabled");
    }
});

builder.Logging.ClearProviders();
builder.Logging.AddEventLog(options =>
{
    options.SourceName = "ANotherRccServiceArbiterLol";
});

if (!WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddConsole();
} else
{
    builder.Host.UseWindowsService(serviceOptions =>
    {
        serviceOptions.ServiceName = "ANotherRccServiceArbiterLol";
    });
}

var app = builder.Build();

Logger.Initialize(app.Logger);

try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
}
catch (Exception ex)
{
    Logger.Warning($"Couldn't set priority: {ex.Message}");
}

_ = Task.Run(async () =>
{
    try
    {
        await RCCServicePool.InitializePool();
        await RCCServicePool.StartPoolMaintenance();
    }
    catch (Exception ex)
    {
        Logger.Critical(ex.ToString());
    }
});

app.Lifetime.ApplicationStopping.Register(RCCServicePool.Shutdown);

app.AddHeaders();
app.UseSwagger();
app.UseSwaggerUI();

if (httpsEnabled)
    app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();

app.Run();