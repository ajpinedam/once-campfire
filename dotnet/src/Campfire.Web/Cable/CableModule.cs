using Campfire.Web.Http;
using Campfire.Web.Views;

namespace Campfire.Web.Cable;

public static class CableModule
{
    public static IServiceCollection AddCableModule(this IServiceCollection services)
    {
        services.AddSingleton<CableServer>();
        services.AddHostedService(provider => provider.GetRequiredService<CableServer>());
        return services;
    }

    /// <summary>
    /// <c>/cable</c>. The connection authenticates itself from the session cookie and answers
    /// unauthenticated clients over the socket, as ActionCable does, so the request pipeline
    /// mustn't redirect or CSRF-check it.
    /// </summary>
    public static IEndpointRouteBuilder MapCableModule(this IEndpointRouteBuilder app)
    {
        app.MapGet(Paths.Cable, (HttpContext context, CableServer cable) => cable.HandleAsync(context))
            .AllowUnauthenticated()
            .SkipCsrf()
            .SkipBrowserCheck();
        return app;
    }
}
