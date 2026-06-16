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

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

Console.Title = "ANotherRccServiceArbiterLol";

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
//app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run($"http://0.0.0.0:{Configuration.GetIntFlag("FIntWebserverPort")}");
