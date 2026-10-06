using Campfire.Web.Configuration;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Platform;
using Campfire.Web.Security;

namespace Campfire.Web.Views;

/// <summary>
/// Everything a page (and its layout) needs from the request, captured as one immutable record
/// so templates never reach into HttpContext. Build it in the handler with <see cref="For"/>.
/// </summary>
public sealed record PageContext(
    User? User,
    Account? Account,
    ApplicationPlatform Platform,
    string CsrfToken,
    FlashMessage? Flash,
    string BaseUrl,
    string RequestUrl,
    string? Referrer,
    long? LastRoomId,
    string? VapidPublicKey,
    string AppVersion,
    bool IsDevelopment)
{
    /// <summary>Rails' <c>Current.user.can_administer?</c> (no record).</summary>
    public bool CanAdminister => User?.CanAdminister() ?? false;

    public bool IsAdministrator => User?.IsAdministrator ?? false;

    /// <summary>The signed-in user; only for pages behind authentication.</summary>
    public User CurrentUser => User ?? throw new InvalidOperationException("This page requires a signed-in user");

    /// <summary>Rails' <c>*_url</c> helpers: an absolute URL for a path.</summary>
    public string Url(string path) => BaseUrl + path;

    public static PageContext For(HttpContext context)
    {
        var services = context.RequestServices;
        var settings = services.GetRequiredService<CampfireSettings>();
        var current = context.GetCurrent();
        var request = context.Request;

        return new PageContext(
            User: current.UserOrNull,
            Account: current.Account,
            Platform: current.Platform,
            CsrfToken: services.GetRequiredService<Csrf>().MaskedToken(context, settings.ForceSsl),
            Flash: services.GetRequiredService<AppCookies>().ConsumeFlash(context),
            BaseUrl: request.BaseUrl(),
            RequestUrl: request.FullUrl(),
            Referrer: request.Headers.Referer is { Count: > 0 } referrer ? referrer.ToString() : null,
            LastRoomId: AppCookies.ReadLastRoom(context),
            VapidPublicKey: settings.VapidPublicKey,
            AppVersion: settings.AppVersion,
            IsDevelopment: settings.IsDevelopment);
    }
}

/// <summary>What the application layout needs from a page.</summary>
public sealed record LayoutModel(PageContext Page, string? Title = null, string? BodyClass = null)
{
    public string BodyClasses => ViewHelpers.ClassNames(
        BodyClass,
        Page.CanAdminister ? "admin" : null,
        Page.Account?.HasLogo == true ? "account-has-logo" : null);
}
