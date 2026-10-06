using System.Text.Json;
using Campfire.Web.Configuration;
using Campfire.Web.Security;

namespace Campfire.Web.Http;

public sealed record FlashMessage(string? Notice, string? Alert)
{
    public string? Message => Notice ?? Alert;
    public bool IsAlert => Alert is not null;
}

/// <summary>
/// The cookies Campfire sets: the signed session token, the flash, where to return after
/// signing in, and the last room visited (Rails keeps the middle two in its session cookie).
/// </summary>
public sealed class AppCookies(KeyRing keys, CampfireSettings settings)
{
    public const string SessionToken = "session_token";
    public const string Flash = "_campfire_flash";
    public const string ReturnTo = "_campfire_return_to";
    public const string LastRoom = "last_room";

    private static readonly object FlashNowKey = new();

    private CookieOptions Options(bool httpOnly = true, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = httpOnly,
        SameSite = SameSiteMode.Lax,
        Secure = settings.ForceSsl,
        Path = "/",
        Expires = expires
    };

    // Session token: cookies.signed.permanent[:session_token] = { httponly: true, same_site: :lax }

    public string? ReadSessionToken(HttpContext context) =>
        keys.Cookies.Verify(context.Request.Cookies[SessionToken]);

    public string? ReadSessionToken(IRequestCookieCollection cookies) => keys.Cookies.Verify(cookies[SessionToken]);

    public void WriteSessionToken(HttpContext context, string token) =>
        context.Response.Cookies.Append(SessionToken, keys.Cookies.Sign(token), Options(expires: DateTimeOffset.UtcNow.AddYears(20)));

    public void DeleteSessionToken(HttpContext context) => context.Response.Cookies.Delete(SessionToken, Options());

    // Flash: survives exactly one redirect

    public void SetFlash(HttpContext context, string? notice = null, string? alert = null) =>
        context.Response.Cookies.Append(Flash, keys.Flash.Sign(JsonSerializer.Serialize(new[] { notice, alert })), Options());

    /// <summary>Rails' <c>flash.now</c>: shown on this response, not stored.</summary>
    public static void SetFlashNow(HttpContext context, string? notice = null, string? alert = null) =>
        context.Items[FlashNowKey] = new FlashMessage(notice, alert);

    /// <summary>Reads (and clears) the flash for the page being rendered.</summary>
    public FlashMessage? ConsumeFlash(HttpContext context)
    {
        if (context.Items[FlashNowKey] is FlashMessage now)
        {
            return now;
        }

        if (context.Request.Cookies[Flash] is not { } stored)
        {
            return null;
        }

        context.Response.Cookies.Delete(Flash, Options());
        if (keys.Flash.Verify(stored) is not { } json)
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<string?[]>(json);
            return values is [var notice, var alert] && (notice ?? alert) is not null ? new FlashMessage(notice, alert) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Return-to URL after authenticating

    public void SetReturnTo(HttpContext context, string url) =>
        context.Response.Cookies.Append(ReturnTo, keys.Cookies.Sign(url), Options());

    /// <summary><c>session.delete(:return_to_after_authenticating) || root_url</c>.</summary>
    public string ConsumeReturnTo(HttpContext context)
    {
        var url = keys.Cookies.Verify(context.Request.Cookies[ReturnTo]);
        context.Response.Cookies.Delete(ReturnTo, Options());
        return url is not null && IsLocal(context, url) ? url : "/";
    }

    // Last room visited: cookies.permanent[:last_room] (unsigned, as in Rails; it's only ever a hint)

    public static long? ReadLastRoom(HttpContext context) =>
        long.TryParse(context.Request.Cookies[LastRoom], out var id) ? id : null;

    public void WriteLastRoom(HttpContext context, long roomId) =>
        context.Response.Cookies.Append(LastRoom, roomId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Options(httpOnly: false, expires: DateTimeOffset.UtcNow.AddYears(20)));

    private static bool IsLocal(HttpContext context, string url) =>
        url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) ||
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
}
