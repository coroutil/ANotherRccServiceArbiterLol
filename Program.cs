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
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

Console.Title = "ANotherRccServiceArbiterLol";

var port = Configuration.GetIntFlag("FIntWebserverPort");
var cert = Path.Combine(AppContext.BaseDirectory, "cert.crt");
var key = Path.Combine(AppContext.BaseDirectory, "cert.key");

var HTTPS = File.Exists(cert) && File.Exists(key);

builder.WebHost.ConfigureKestrel(options =>
{
    if (HTTPS)
    {
        var fakeahcert = X509Certificate2.CreateFromPemFile(cert, key);

        options.ListenAnyIP(port, listen =>
        {
            listen.UseHttps(fakeahcert);
        });

        Console.WriteLine("HTTPS enabled");
    }
    else
    {
        options.ListenAnyIP(port);
        Console.WriteLine("HTTP only");
    }
});

var app = builder.Build();

Configuration.Initialize(app.Configuration); // build fflags

// now we start rccservice
await RCCServicePool.InitializePool();
_ = Task.Run(RCCServicePool.StartPoolMaintenance);

app.Lifetime.ApplicationStopping.Register(() =>
{
    RCCServicePool.Shutdown();
});

// custom headers (middleware)
app.AddHeaders();
// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();
if (HTTPS)
{
    app.UseHttpsRedirection();
}
app.UseAuthorization();
app.MapControllers();
app.Run($"http://0.0.0.0:{Configuration.GetIntFlag("FIntWebserverPort")}");
