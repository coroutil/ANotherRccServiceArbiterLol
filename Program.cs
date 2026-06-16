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
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

Configuration.Initialize(app.Configuration); // build fflags

// now we start rccservice
await RCCServicePool.InitializePool();
_ = Task.Run(RCCServicePool.StartPoolMaintenance);

app.Lifetime.ApplicationStopping.Register(() =>
{
    RCCServicePool.Shutdown();
});

app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();

    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Response-Time"] = $"{sw.ElapsedMilliseconds}ms";
        context.Response.Headers["X-Request-Id"] = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Server-Time"] = DateTime.UtcNow.ToString("O");
        context.Response.Headers["X-Powered-By"] = "ANotherRccServiceArbiterLol";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.Headers["Machine-Id"] = Helper.GetNodeId();
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
        return Task.CompletedTask;
    });

    await next();

    sw.Stop();
});

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();
//app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run($"http://0.0.0.0:{Configuration.GetIntFlag("FIntWebserverPort")}");
