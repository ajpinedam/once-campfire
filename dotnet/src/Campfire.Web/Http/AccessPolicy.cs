namespace Campfire.Web.Http;

/// <summary>
/// Endpoint metadata replacing the Rails controller macros. Endpoints with no policy require a
/// signed-in human (session cookie), deny bots, and verify CSRF tokens on unsafe methods.
/// </summary>
public sealed record AccessPolicy(
    bool AllowUnauthenticated = false,
    bool RequireUnauthenticated = false,
    bool AllowBots = false,
    bool SkipCsrf = false,
    bool SkipBrowserCheck = false)
{
    public static readonly AccessPolicy Default = new();
}

public static class AccessPolicyExtensions
{
    /// <summary>Rails' <c>allow_unauthenticated_access</c>: the session isn't even looked up.</summary>
    public static TBuilder AllowUnauthenticated<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.Update(policy => policy with { AllowUnauthenticated = true });

    /// <summary>Rails' <c>require_unauthenticated_access</c>: signed-in users are sent to the root.</summary>
    public static TBuilder RequireUnauthenticated<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.Update(policy => policy with { RequireUnauthenticated = true });

    /// <summary>Rails' <c>allow_bot_access</c>: a <c>{bot_key}</c> route value authenticates a bot.</summary>
    public static TBuilder AllowBots<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.Update(policy => policy with { AllowBots = true });

    /// <summary>Rails' <c>skip_forgery_protection</c>.</summary>
    public static TBuilder SkipCsrf<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.Update(policy => policy with { SkipCsrf = true });

    public static TBuilder SkipBrowserCheck<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.Update(policy => policy with { SkipBrowserCheck = true });

    private static TBuilder Update<TBuilder>(this TBuilder builder, Func<AccessPolicy, AccessPolicy> change) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            var existing = endpoint.Metadata.OfType<AccessPolicy>().LastOrDefault() ?? AccessPolicy.Default;
            endpoint.Metadata.Add(change(existing));
        });
        return builder;
    }
}
