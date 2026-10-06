using System.Collections.Frozen;
using AngleSharp.Dom;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Security;
using Campfire.Web.Views;

namespace Campfire.Web.RichText;

using Campfire.Web.Data.Queries;

/// <summary>
/// Action Text, as Campfire uses it: message bodies are HTML from the Lexxy editor with
/// <c>&lt;action-text-attachment&gt;</c> nodes for @mentions (sgid → User) and link previews
/// (OpenGraph embeds). This service stores, renders, sanitizes and flattens those bodies.
/// Thread-safe singleton.
/// </summary>
public sealed class RichTextService
{
    private static readonly FrozenSet<string> AttachmentAttributes = SafeList.AttachmentAttributes.ToFrozenSet(StringComparer.Ordinal);

    private readonly SignedGlobalIds _sgids;

    public RichTextService(KeyRing keys) => _sgids = new SignedGlobalIds(keys);

    /// <summary>
    /// The canonical form to store for a submitted body (ActionText's canonicalization): each
    /// attachment node keeps only its identifying attributes (sgid / content-type / content, plus
    /// legacy Trix attributes) with no inner HTML. Bodies from bots are plain text or HTML.
    /// </summary>
    public string Canonicalize(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        var root = HtmlDom.Parse(html);
        foreach (var node in HtmlDom.Attachments(root))
        {
            HtmlDom.SetInnerHtml(node, "");
        }
        return HtmlDom.Serialize(root);
    }

    /// <summary>
    /// Everything posting needs from a submitted body, from one parse: the canonical HTML to store,
    /// its plain text (search, pushes, sounds) and the users it mentions (webhooks). Equivalent to
    /// <see cref="Canonicalize"/> then <see cref="ToPlainText"/> and <see cref="MentionedUserIds"/>
    /// on the result, which would parse the body three times.
    /// </summary>
    public PreparedBody Prepare(Sql sql, string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return PreparedBody.Empty;
        }

        var root = HtmlDom.Parse(html);
        var nodes = HtmlDom.Attachments(root);
        var mentioned = new List<long>();
        foreach (var node in nodes)
        {
            HtmlDom.SetInnerHtml(node, "");
            if (_sgids.VerifiedUserId(node.GetAttribute("sgid")) is { } id && !mentioned.Contains(id))
            {
                mentioned.Add(id);
            }
        }

        var canonical = HtmlDom.Serialize(root);
        var plainText = PlainText.Convert(root, Load(sql, nodes, requestHost: "").PlainText);
        return new PreparedBody(canonical, plainText, mentioned);
    }

    /// <summary>
    /// ActionText's <c>to_plain_text</c>: text with block elements as lines, mentions as
    /// <c>@Name</c>, link previews as nothing. Used for search indexing, push notifications,
    /// webhooks and sound detection.
    /// </summary>
    public string ToPlainText(Sql sql, string storedHtml)
    {
        if (string.IsNullOrEmpty(storedHtml))
        {
            return "";
        }

        var root = HtmlDom.Parse(storedHtml);
        var attachables = Load(sql, HtmlDom.Attachments(root), requestHost: "");
        return PlainText.Convert(root, attachables.PlainText);
    }

    /// <summary>
    /// Ids of users mentioned in a body, distinct, in order of appearance. Like Action Text's
    /// <c>Content#attachables</c>, only sgids that verify count — a forged or stale sgid renders as
    /// a mention but never notifies anyone.
    /// </summary>
    public IReadOnlyList<long> MentionedUserIds(string storedHtml)
    {
        if (string.IsNullOrEmpty(storedHtml) || !storedHtml.Contains(HtmlDom.AttachmentTag, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var ids = new List<long>();
        foreach (var node in HtmlDom.Attachments(HtmlDom.Parse(storedHtml)))
        {
            if (_sgids.VerifiedUserId(node.GetAttribute("sgid")) is { } id && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>
    /// Rails' <c>message_presentation</c> for a text message: the body rendered inside
    /// <c>&lt;div class="lexxy-content"&gt;</c> with attachments rendered (mention partial, OpenGraph
    /// embed partial), then RemoveSoloUnfurledLinkText / SanitizeTags / SanitizeAttributes,
    /// then <c>auto_link</c> (target=_blank) and the final safe-list sanitize.
    /// <paramref name="requestHost"/> is the host Campfire is served from (embeds may not point at it).
    /// </summary>
    public string RenderPresentation(Sql sql, string storedHtml, string requestHost)
    {
        var host = NormalizeHost(requestHost);
        var root = HtmlDom.Parse(storedHtml);
        var attachables = Load(sql, HtmlDom.Attachments(root), host);

        // ContentFilters::TextMessagePresentationFilters, applied to the stored content
        ContentFilters.RemoveSoloUnfurledLinkText(root, attachables);
        ContentFilters.SanitizeTags(root);
        Sanitizers.ContentFilter.Scrub(root);

        // h(content): Content#to_s renders attachments into the layout, through Action Text's sanitizer
        RenderAttachments(root, attachables, keepContentAttributes: false);
        Sanitizers.ActionText.Scrub(root);
        WrapInLayout(root);

        // auto_link re-sanitizes with AUTO_LINK_ALLOWED_TAGS/ATTRIBUTES, then links URLs and emails
        Sanitizers.AutoLink.Scrub(root);
        return AutoLink.Apply(HtmlDom.Serialize(root));
    }

    /// <summary>
    /// <c>ContentFilters::TextMessagePresentationFilters.apply(message.body.body).to_html</c>: the
    /// stored content after the presentation filters, before rendering (tests assert on this stage).
    /// </summary>
    internal string ApplyPresentationFilters(Sql sql, string storedHtml, string requestHost)
    {
        var root = HtmlDom.Parse(storedHtml);
        var attachables = Load(sql, HtmlDom.Attachments(root), NormalizeHost(requestHost));
        ContentFilters.RemoveSoloUnfurledLinkText(root, attachables);
        ContentFilters.SanitizeTags(root);
        Sanitizers.ContentFilter.Scrub(root);
        return HtmlDom.Serialize(root);
    }

    /// <summary>Resolves one attachment node as Action Text would (tests).</summary>
    internal (OpengraphEmbed? Embed, User? User) ResolveAttachment(Sql sql, string attachmentHtml, string requestHost)
    {
        var nodes = HtmlDom.Attachments(HtmlDom.Parse(attachmentHtml));
        return Load(sql, nodes, NormalizeHost(requestHost)).Resolve(nodes[0]);
    }

    /// <summary><c>message.body.to_s</c>: the rendered body with layout and attachments, unfiltered (API JSON).</summary>
    public string RenderBody(Sql sql, string storedHtml, string requestHost)
    {
        var root = HtmlDom.Parse(storedHtml);
        var attachables = Load(sql, HtmlDom.Attachments(root), NormalizeHost(requestHost));

        RenderAttachments(root, attachables, keepContentAttributes: true);
        Sanitizers.ActionText.Scrub(root);
        WrapInLayout(root);
        return HtmlDom.Serialize(root);
    }

    /// <summary>
    /// Rails' <c>editable_body</c>: the stored body with every attachment rebuilt from its attachable
    /// (content-type set, <c>content</c> attribute holding the rendered partial) for the Lexxy editor.
    /// The editor keeps an attachment's content as it finds it, so a Trix-era embed's node
    /// attributes, a mention's generic Trix content type, or hand-written embed markup would
    /// otherwise survive into the edit.
    /// </summary>
    public string EditableBody(Sql sql, string storedHtml, string requestHost)
    {
        var root = HtmlDom.Parse(storedHtml);
        var nodes = HtmlDom.Attachments(root);
        if (nodes.Length == 0)
        {
            return HtmlDom.Serialize(root);
        }

        var attachables = Load(sql, nodes, NormalizeHost(requestHost));
        foreach (var node in nodes)
        {
            var (embed, user) = attachables.Resolve(node);
            if (user is not null)
            {
                node.SetAttribute("content-type", User.MentionContentType);
                node.SetAttribute("content", RenderMention(user));
            }
            else if (embed is not null)
            {
                node.SetAttribute("content-type", OpengraphEmbed.ContentType);
                node.SetAttribute("content", embed.Render());
            }

            HtmlDom.SetInnerHtml(node, ""); // RichText.new(body:) canonicalizes again
        }

        return HtmlDom.Serialize(root);
    }

    /// <summary>The sgid identifying a user as a mention attachable (<c>user.attachable_sgid</c>).</summary>
    public string UserSgid(long userId) => _sgids.ForUser(userId);

    /// <summary>
    /// The user id an sgid refers to. Accepts sgids signed by this app, and — like the Rails app's
    /// attachable_from_possibly_expired_sgid — Rails-format sgids whose signature can't be verified,
    /// but only for User records.
    /// </summary>
    public long? UserIdFromSgid(string? sgid) => SignedGlobalIds.UnverifiedUserId(sgid);

    /// <summary>The <c>users/_mention</c> partial: <c>&lt;span class="mention" sgid=...&gt;{avatar} Name&lt;/span&gt;</c>.</summary>
    /// <remarks>A span rather than a div: mentions render inline inside the paragraphs the editor
    /// produces, and block elements would make the browser split the paragraph when parsing.</remarks>
    public string RenderMention(User user) =>
        $"<span class=\"mention\" sgid=\"{MinimalHtmlEncoder.Escape(UserSgid(user.Id))}\">{ViewHelpers.AvatarTag(user).Value} {MinimalHtmlEncoder.Escape(user.Name)}</span>";

    /// <summary>Rails' <c>String#all_emoji?</c>, for the <c>message--emoji</c> class.</summary>
    public static bool IsAllEmoji(string text) => Emoji.IsAllEmoji(text);

    /// <summary>
    /// Content#render_attachments(with_full_attributes: true) + render_action_text_attachment:
    /// each node is rebuilt from Action Text's attachment attributes (plus a fresh sgid and the
    /// mention content type for users) and filled with the attachable's partial.
    /// </summary>
    private void RenderAttachments(IElement root, Attachables attachables, bool keepContentAttributes)
    {
        foreach (var node in HtmlDom.Attachments(root))
        {
            if (node.Parent is not { } parent)
            {
                continue;
            }

            // Content is sanitized before anything reads it (Rails' sanitize_content_attachment).
            // Only embeds read it, and presentation drops the attribute anyway, so skip the work then.
            if (node.GetAttribute("content") is { } content && (keepContentAttributes || OpengraphEmbed.IsEmbedNode(node)))
            {
                node.RemoveAttribute("content");
                var sanitized = Sanitizers.ActionText.Sanitize(content);
                if (!string.IsNullOrWhiteSpace(sanitized))
                {
                    node.SetAttribute("content", sanitized);
                }
            }

            var (embed, user) = attachables.Resolve(node);
            var rendered = HtmlDom.ShallowCopy(node, AttachmentAttributes);
            string partial;
            if (user is not null)
            {
                rendered.SetAttribute("sgid", UserSgid(user.Id));
                rendered.SetAttribute("content-type", User.MentionContentType);
                partial = RenderMention(user);
            }
            else
            {
                partial = embed?.Render() ?? "";
            }

            HtmlDom.SetInnerHtml(rendered, partial);
            parent.ReplaceChild(rendered, node);
        }
    }

    /// <summary>layouts/action_text/contents/_content: <c>&lt;div class="lexxy-content"&gt;</c> around the content.</summary>
    private static void WrapInLayout(IElement root)
    {
        var layout = root.Owner!.CreateElement("div");
        layout.SetAttribute("class", "lexxy-content");
        foreach (var child in root.ChildNodes.ToArray())
        {
            layout.AppendChild(child);
        }
        root.AppendChild(layout);
    }

    /// <summary>Resolves attachment nodes, loading every mentioned user in one query.</summary>
    private static Attachables Load(Sql? sql, IElement[] nodes, string requestHost)
    {
        if (sql is null || nodes.Length == 0)
        {
            return new Attachables(FrozenDictionary<long, User>.Empty, requestHost);
        }

        var ids = new HashSet<long>();
        foreach (var node in nodes)
        {
            if (!OpengraphEmbed.IsEmbedNode(node) && SignedGlobalIds.UnverifiedUserId(node.GetAttribute("sgid")) is { } id)
            {
                ids.Add(id);
            }
        }

        var users = ids.Count == 0 ? [] : Users.FindMany(sql, ids).ToDictionary(user => user.Id);
        return new Attachables(users, requestHost);
    }

    /// <summary>The bare host name of "host", "host:port", "[::1]:port" or a full URL.</summary>
    internal static string NormalizeHost(string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return "";
        }

        if (requestHost.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(requestHost, UriKind.Absolute, out var uri) ? uri.Host : "";
        }

        return new HostString(requestHost).Host;
    }
}

/// <summary>
/// The app's <c>ActionText::Attachment.from_node</c> override, per render: an OpenGraph embed
/// first, then a user from a (possibly expired) sgid; anything else is a missing attachable.
/// </summary>
internal sealed class Attachables(IReadOnlyDictionary<long, User> users, string requestHost)
{
    public string RequestHost => requestHost;

    public (OpengraphEmbed? Embed, User? User) Resolve(IElement node)
    {
        if (OpengraphEmbed.FromNode(node, requestHost) is { } embed)
        {
            return (embed, null);
        }

        if (SignedGlobalIds.UnverifiedUserId(node.GetAttribute("sgid")) is { } id && users.TryGetValue(id, out var user))
        {
            return (null, user);
        }

        return (null, null);
    }

    /// <summary><c>Attachment#to_plain_text</c>: mentions read "@Name", embeds nothing, missing ones their caption.</summary>
    public string PlainText(IElement node)
    {
        var (embed, user) = Resolve(node);
        return user is not null ? user.MentionText : embed is not null ? "" : node.GetAttribute("caption") ?? "";
    }
}

/// <summary>A submitted body, canonicalized for storage, with what posting derives from it.</summary>
public sealed record PreparedBody(string Canonical, string PlainText, IReadOnlyList<long> MentionedUserIds)
{
    public static readonly PreparedBody Empty = new("", "", []);
}
