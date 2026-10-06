using Campfire.Web.Domain;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Views.Accounts;

/// <summary>accounts/edit.</summary>
public sealed record AccountEditModel(
    PageContext Page,
    Account Account,
    IReadOnlyList<User> Administrators,
    IReadOnlyList<User> Members,
    int? NextPage,
    string BackPath);

/// <summary>accounts/users/_user.</summary>
public sealed record AccountUserModel(PageContext Page, User User);

/// <summary>accounts/_help_contact.</summary>
public sealed record HelpContactModel(User? Owner, string AppVersion);

/// <summary>accounts/custom_styles/edit.</summary>
public sealed record CustomStylesModel(PageContext Page, Account Account);

/// <summary>Helpers the people/account templates share (Rails' ApplicationHelper bits).</summary>
public static class PeopleHelpers
{
    /// <summary>Rails' <c>link_back</c>: back to the referrer, or home when there's none (or it's this page).</summary>
    public static HtmlString LinkBack(PageContext page) =>
        ViewHelpers.LinkBackTo(page.Referrer is { Length: > 0 } referrer && referrer != page.RequestUrl ? referrer : Paths.Root);

    /// <summary>Rails' <c>version_badge</c>.</summary>
    public static HtmlString VersionBadge(string version) =>
        new($"<span class=\"version-badge\">{ViewHelpers.Escape(version)}</span>");

    /// <summary>Rails' <c>turbo_frame_tag :next_page_container</c> for the lazy-loaded next page of people.</summary>
    public static HtmlString NextPageContainer(int page) =>
        new($"<turbo-frame loading=\"lazy\" src=\"{Paths.AccountUsers}.turbo_stream?page={page}\" class=\"flex center\" id=\"next_page_container\"><div class=\"spinner center\"></div></turbo-frame>");

    /// <summary>The <c>&lt;span class="for-screen-reader"&gt;</c> pairing used inside icon buttons.</summary>
    public static HtmlString IconContent(string icon, string screenReaderText, int size = 20, string? iconClass = null) =>
        new(ViewHelpers.ImageTag(icon, new Img(Size: size, AriaHidden: true, Class: iconClass)).Value +
            $"<span class=\"for-screen-reader\">{ViewHelpers.Escape(screenReaderText)}</span>");
}
