using System.Diagnostics;

namespace Arbiter.Middleware;

public sealed class Middleware
{
    private readonly RequestDelegate _next;

    public Middleware(RequestDelegate next) {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context) {
        var sw = Stopwatch.StartNew();

        context.Response.OnStarting(() => {
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

        await _next(context);

        sw.Stop();
    }
}