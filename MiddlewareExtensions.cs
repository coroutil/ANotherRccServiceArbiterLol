namespace Arbiter.Middleware;

public static class MiddlewareExtensions
{
    public static IApplicationBuilder AddHeaders(this IApplicationBuilder app) {
        return app.UseMiddleware<Middleware>();
    }
}