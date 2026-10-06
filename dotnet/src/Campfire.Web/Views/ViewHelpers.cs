using System.Globalization;
using System.Text;
using Campfire.Web.Assets;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Security;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Views;

/// <summary>Options for <see cref="ViewHelpers.ImageTag"/> (Rails' <c>image_tag</c> options).</summary>
public sealed record Img(
    int? Size = null,
    int? Width = null,
    int? Height = null,
    string? Alt = null,
    string? Class = null,
    string? Style = null,
    bool AriaHidden = false,
    string? AriaLabel = null,
    string? Role = null,
    string? Loading = null,
    bool Hidden = false,
    string? Data = null)
{
    public static readonly Img None = new();
}

/// <summary>
/// View helpers shared by every template, imported with <c>@using static</c> so templates call
/// <c>@ImageTag(...)</c> like ERB. (A class named <c>Html</c> would collide with the MVC <c>Html</c>
/// property the Razor compiler injects into every template.)
/// (Rails' ApplicationHelper and friends). All return
/// <see cref="HtmlString"/>, which Razor writes without re-encoding; every interpolated value
/// is escaped here.
/// </summary>
public static class ViewHelpers
{
    private static MessageSigner? _streamNames;

    public static void Configure(KeyRing keys) => _streamNames = keys.StreamNames;

    public static HtmlString Raw(string? html) => new(html ?? "");

    public static string Escape(string? text) => MinimalHtmlEncoder.Escape(text);

    /// <summary>Rails' <c>asset_path</c>.</summary>
    public static string AssetPath(string logicalPath) => AssetCatalog.Current.PathFor(logicalPath);

    /// <summary>Rails' <c>image_tag</c> for an asset (<c>"check.svg"</c>) or an absolute/rooted URL.</summary>
    public static HtmlString ImageTag(string source, Img? options = null)
    {
        options ??= Img.None;
        var src = source.StartsWith('/') || source.Contains("://", StringComparison.Ordinal) ? source : AssetPath(source);

        var html = new StringBuilder("<img");
        Attr(html, "alt", options.Alt);
        Attr(html, "class", options.Class);
        Attr(html, "style", options.Style);
        if (options.AriaHidden) html.Append(" aria-hidden=\"true\"");
        Attr(html, "aria-label", options.AriaLabel);
        Attr(html, "role", options.Role);
        Attr(html, "loading", options.Loading);
        if (options.Hidden) html.Append(" hidden=\"hidden\"");
        if (options.Data is { } data) html.Append(' ').Append(data);
        var width = options.Width ?? options.Size;
        var height = options.Height ?? options.Size;
        if (width is { } w) html.Append(" width=\"").Append(w.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (height is { } h) html.Append(" height=\"").Append(h.ToString(CultureInfo.InvariantCulture)).Append('"');
        Attr(html, "src", src);
        return new HtmlString(html.Append(" />").ToString());
    }

    /// <summary>Rails' <c>avatar_tag</c>: the user's avatar linking to their profile, breaking out of frames.</summary>
    public static HtmlString AvatarTag(User user, string? loading = null, string? ariaLabel = null)
    {
        var html = new StringBuilder("<a title=\"").Append(Escape(user.Title))
            .Append("\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\"").Append(Paths.User(user.Id)).Append("\">");
        html.Append(ImageTag(Paths.FreshUserAvatar(user), new Img(Size: 48, AriaHidden: ariaLabel is null, AriaLabel: ariaLabel, Loading: loading)).Value);
        return new HtmlString(html.Append("</a>").ToString());
    }

    /// <summary>Rails' <c>account_logo_tag</c>.</summary>
    public static HtmlString AccountLogoTag(Account? account, string? style = null) =>
        new($"<figure class=\"account-logo avatar {Escape(style)}\">{ImageTag(Paths.FreshAccountLogo(account), new Img(Alt: "Account logo", Size: 300)).Value}</figure>");

    /// <summary>Rails' <c>local_datetime_tag</c>; the local-time controller fills in the text.</summary>
    public static HtmlString LocalDatetimeTag(DateTime time, string style = "time", string? cssClass = null) =>
        new($"<time{(cssClass is null ? "" : $" class=\"{Escape(cssClass)}\"")} datetime=\"{SqlTime.ToIso8601(time)}\" data-local-time-target=\"{style}\"></time>");

    /// <summary>
    /// <c>turbo_stream_from</c>: subscribes the page to a stream through the given cable channel.
    /// The stream name is signed so clients can't subscribe to streams they weren't handed.
    /// </summary>
    public static HtmlString TurboStreamFrom(string streamName, string channel = "Turbo::StreamsChannel")
    {
        var signer = _streamNames ?? throw new InvalidOperationException("ViewHelpers.Configure must run at startup");
        return new HtmlString($"<turbo-cable-stream-source channel=\"{Escape(channel)}\" signed-stream-name=\"{Escape(signer.Sign(streamName))}\"></turbo-cable-stream-source>");
    }

    /// <summary>The hidden CSRF field forms carry (omit inside cached fragments; Turbo sends the header).</summary>
    public static HtmlString CsrfField(string? token) =>
        token is null ? HtmlString.Empty : new($"<input type=\"hidden\" name=\"authenticity_token\" value=\"{Escape(token)}\" autocomplete=\"off\" />");

    /// <summary>The <c>_method</c> override Rails forms use for PATCH/PUT/DELETE.</summary>
    public static HtmlString MethodField(string method) =>
        method.Equals("post", StringComparison.OrdinalIgnoreCase) || method.Equals("get", StringComparison.OrdinalIgnoreCase)
            ? HtmlString.Empty
            : new($"<input type=\"hidden\" name=\"_method\" value=\"{Escape(method.ToLowerInvariant())}\" autocomplete=\"off\" />");

    /// <summary>
    /// Rails' <c>button_to</c>: a one-button form. <paramref name="buttonAttributes"/> is raw,
    /// pre-escaped attribute markup for the button (data-*, aria-*, ...).
    /// </summary>
    public static HtmlString ButtonTo(
        string url,
        HtmlString content,
        string method = "post",
        string? cssClass = null,
        string? csrfToken = null,
        string? confirm = null,
        string? ariaLabel = null,
        string? buttonAttributes = null,
        string? formAttributes = null)
    {
        var html = new StringBuilder("<form class=\"button_to\" method=\"post\" action=\"").Append(Escape(url)).Append('"');
        if (formAttributes is not null) html.Append(' ').Append(formAttributes);
        html.Append('>').Append(MethodField(method).Value).Append("<button");
        Attr(html, "class", cssClass);
        Attr(html, "aria-label", ariaLabel);
        Attr(html, "data-turbo-confirm", confirm);
        if (buttonAttributes is not null) html.Append(' ').Append(buttonAttributes);
        html.Append(" type=\"submit\">").Append(content.Value).Append("</button>").Append(CsrfField(csrfToken).Value).Append("</form>");
        return new HtmlString(html.ToString());
    }

    /// <summary>Rails' <c>link_back_to</c>.</summary>
    public static HtmlString LinkBackTo(string destination) =>
        new($"<a class=\"btn\" href=\"{Escape(destination)}\">{ImageTag("arrow-left.svg", new Img(Size: 20, AriaHidden: true)).Value}<span class=\"for-screen-reader\">Go Back</span></a>");

    /// <summary>Rails' <c>translation_button</c>: the globe popup listing a prompt in several languages.</summary>
    public static HtmlString TranslationButton(string key) => Translations.Button(key);

    /// <summary>Escapes and appends <c> name="value"</c> when value isn't null.</summary>
    public static void Attr(StringBuilder html, string name, string? value)
    {
        if (value is not null)
        {
            html.Append(' ').Append(name).Append("=\"").Append(Escape(value)).Append('"');
        }
    }

    /// <summary>Joins CSS classes, skipping blanks (Rails' <c>class: [ ..., { "x": cond } ]</c>).</summary>
    public static string ClassNames(params ReadOnlySpan<string?> classes)
    {
        var html = new StringBuilder();
        foreach (var name in classes)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (html.Length > 0) html.Append(' ');
                html.Append(name);
            }
        }
        return html.ToString();
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Epoch(DateTime time) => SqlTime.ToEpochMilliseconds(time).ToString(CultureInfo.InvariantCulture);

    /// <summary>Rails' <c>truncate(text, length:, omission: "…")</c>.</summary>
    public static string Truncate(string? text, int length, string omission = "…")
    {
        if (text is null || text.Length <= length)
        {
            return text ?? "";
        }
        return string.Concat(text.AsSpan(0, Math.Max(0, length - omission.Length)), omission);
    }
}
