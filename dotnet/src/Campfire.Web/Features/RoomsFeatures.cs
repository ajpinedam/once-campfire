using Campfire.Web.Features.Autocomplete;
using Campfire.Web.Features.Boosts;
using Campfire.Web.Features.Messages;
using Campfire.Web.Features.Rooms;
using Campfire.Web.Features.Searches;
using Campfire.Web.Features.Sidebar;
using Campfire.Web.Features.Welcome;

namespace Campfire.Web.Features;

/// <summary>
/// The Rooms & Messages workstream: rooms (show/index/destroy, opens, closeds, directs,
/// involvements, refreshes, @message), messages (HTML + bot JSON API), boosts, sidebar,
/// searches, welcome, autocompletable users.
/// </summary>
public static class RoomsFeatures
{
    public static IServiceCollection AddRoomsFeatures(this IServiceCollection services)
    {
        services.AddSingleton<MessageRenderer>();
        services.AddSingleton<MessagePosting>();
        services.AddSingleton<MessageBroadcasts>();
        services.AddSingleton<BoostBroadcasts>();
        services.AddSingleton<RoomBroadcasts>();

        // Bot bodies are raw text that form parsing would otherwise consume first
        services.AddTransient<IStartupFilter, RawRequestBody.BufferingFilter>();
        return services;
    }

    public static IEndpointRouteBuilder MapRoomsFeatures(this IEndpointRouteBuilder app)
    {
        app.MapWelcome();
        app.MapRooms();
        app.MapMessages();
        app.MapBotMessages();
        app.MapBoosts();
        app.MapSidebar();
        app.MapSearches();
        app.MapAutocomplete();
        return app;
    }
}
