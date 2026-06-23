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
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

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
        Console.WriteLine("HTTPS enabled");
    }
    else
    {
        options.ListenAnyIP(port);
        Console.WriteLine("HTTP only");
    }
});

var app = builder.Build();

await RCCServicePool.InitializePool();
_ = Task.Run(RCCServicePool.StartPoolMaintenance);

app.Lifetime.ApplicationStopping.Register(RCCServicePool.Shutdown);

app.AddHeaders();
app.UseSwagger();
app.UseSwaggerUI();

if (httpsEnabled)
    app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();

app.Run();