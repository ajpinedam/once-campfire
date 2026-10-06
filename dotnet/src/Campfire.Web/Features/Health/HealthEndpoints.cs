using Campfire.Web.Http;

namespace Campfire.Web.Features.Health;

/// <summary>Rails' <c>rails/health#show</c> at <c>/up</c>: 200 once the app has booted.</summary>
public static class HealthEndpoints
{
    private const string Up = """<!DOCTYPE html><html><body style="background-color: green"></body></html>""";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/up", () => Results.Content(Up, "text/html; charset=utf-8"))
            .AllowUnauthenticated()
            .SkipBrowserCheck();
        return app;
    }
}
