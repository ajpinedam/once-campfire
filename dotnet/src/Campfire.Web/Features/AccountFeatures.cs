using Campfire.Web.Features.AccountSettings;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Features.FirstRun;
using Campfire.Web.Features.Health;
using Campfire.Web.Features.People;
using Campfire.Web.Features.Profiles;
using Campfire.Web.Features.Pwa;
using Campfire.Web.Features.QrCodeImages;
using Campfire.Web.Features.Sessions;

namespace Campfire.Web.Features;

/// <summary>
/// The Accounts & People workstream: sessions (+ transfers, rate limit, incompatible browser),
/// first run, join/sign up, users, bans, profiles, avatars, account settings (people, bots, bot
/// keys, join code, logo, custom styles), PWA manifest/service worker, QR codes, health check.
/// </summary>
public static class AccountFeatures
{
    public static IServiceCollection AddAccountFeatures(this IServiceCollection services)
    {
        services.AddSingleton<UserModeration>();
        services.AddSessionsRateLimit();
        return services;
    }

    public static IEndpointRouteBuilder MapAccountFeatures(this IEndpointRouteBuilder app)
    {
        app.MapSessions();
        app.MapFirstRun();
        app.MapPeople();
        app.MapAvatars();
        app.MapProfiles();
        app.MapAccountSettings();
        app.MapBots();
        app.MapPwa();
        app.MapQrCodes();
        app.MapHealth();
        return app;
    }
}
