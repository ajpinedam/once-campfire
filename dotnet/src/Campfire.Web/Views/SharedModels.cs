using System.Buffers.Text;
using System.Text;
using Campfire.Web.Platform;

namespace Campfire.Web.Views;

/// <summary>Model for <c>Views/Shared/Invite.cshtml</c> (accounts/_invite).</summary>
public sealed record InviteModel(PageContext Page)
{
    public string JoinUrl => Page.Url(Paths.Join(Page.Account?.JoinCode ?? ""));
}

/// <summary>Model for the notification help partials in <c>Views/Pwa</c>.</summary>
public sealed record PwaHelpModel(ApplicationPlatform Platform, string RootUrl);

/// <summary>Model for <c>Views/Pwa/OsNotificationSteps.cshtml</c>.</summary>
public sealed record OsNotificationStepsModel(ApplicationPlatform Platform, string Browser, string SwitchAlt);

public static class QrCodes
{
    /// <summary>Rails' <c>Base64.urlsafe_encode64(url)</c> (padded), the id in <c>/qr_code/:id</c>.</summary>
    public static string Id(string url)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
        return encoded.Replace('+', '-').Replace('/', '_');
    }

    public static string? UrlFromId(string id)
    {
        try
        {
            var normalized = id.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Path(string url) => Paths.QrCode(Id(url));
}
