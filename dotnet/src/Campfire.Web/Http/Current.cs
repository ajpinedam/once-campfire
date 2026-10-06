using System.Net;
using Campfire.Web.Domain;
using Campfire.Web.Platform;

namespace Campfire.Web.Http;

public enum AuthenticatedBy
{
    None,
    Session,
    BotKey
}

/// <summary>
/// Per-request state (Rails' <c>Current</c>): who is signed in, how, and on which account.
/// Created by <see cref="CampfireRequestMiddleware"/>; handlers receive it as a parameter
/// (minimal APIs bind it through <see cref="BindAsync"/>).
/// </summary>
public sealed class Current
{
    private ApplicationPlatform? _platform;

    internal Current(HttpContext httpContext, Account? account)
    {
        HttpContext = httpContext;
        Account = account;
    }

    public HttpContext HttpContext { get; }

    /// <summary>The single account. Null only before the first-run wizard has completed.</summary>
    public Account? Account { get; internal set; }

    public User? UserOrNull { get; internal set; }
    public Session? Session { get; internal set; }
    public AuthenticatedBy AuthenticatedBy { get; internal set; }

    /// <summary>The signed-in user; only call on endpoints that require authentication.</summary>
    public User User => UserOrNull ?? throw new InvalidOperationException("No signed-in user on this request");

    public bool SignedIn => UserOrNull is not null;

    public ApplicationPlatform Platform => _platform ??= ApplicationPlatform.Parse(HttpContext.Request.Headers.UserAgent);

    /// <summary>Rails' <c>request.remote_ip</c>, with IPv4-mapped IPv6 addresses unwrapped.</summary>
    public string? RemoteIp => RemoteIpOf(HttpContext);

    public string? UserAgent => HttpContext.Request.Headers.UserAgent is { Count: > 0 } agent ? agent.ToString() : null;

    public Account RequiredAccount => Account ?? throw new InvalidOperationException("Campfire hasn't been set up yet");

    public static string? RemoteIpOf(HttpContext context) => context.Connection.RemoteIpAddress switch
    {
        null => null,
        { IsIPv4MappedToIPv6: true } address => address.MapToIPv4().ToString(),
        IPAddress address => address.ToString()
    };

    public static ValueTask<Current?> BindAsync(HttpContext context) => ValueTask.FromResult<Current?>(context.GetCurrent());
}

public static class CurrentExtensions
{
    private static readonly object Key = new();

    public static Current GetCurrent(this HttpContext context) =>
        context.Items[Key] as Current ?? throw new InvalidOperationException("Current is set by CampfireRequestMiddleware");

    internal static Current? TryGetCurrent(this HttpContext context) => context.Items[Key] as Current;

    internal static void SetCurrent(this HttpContext context, Current current) => context.Items[Key] = current;
}
