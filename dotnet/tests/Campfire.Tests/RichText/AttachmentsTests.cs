using System.Net;
using System.Text;
using Campfire.Tests.Support;
using Campfire.Web.RichText;

namespace Campfire.Tests.RichText;

/// <summary>
/// Port of test/models/action_text_attachment_test.rb, test/lib/rails_ext/action_text_attachables_test.rb,
/// test/lib/rails_ext/actiontext_opengraph_embeds_test.rb and test/helpers/rich_text_helper_test.rb.
/// </summary>
public sealed class AttachmentsTests(CampfireApp app) : RichTextTestBase(app), IClassFixture<CampfireApp>
{
    private (OpengraphEmbed? Embed, Campfire.Web.Domain.User? User) Resolve(string html, string host = Host) =>
        App.Sql(sql => RichText.ResolveAttachment(sql, html, host));

    // Users from sgids

    [Fact]
    public void From_node_finds_the_user_for_an_sgid()
    {
        var sgid = RichText.UserSgid(Fixtures.David.Id);
        Assert.Equal(Fixtures.David.Id, Resolve($"<action-text-attachment sgid=\"{sgid}\"></action-text-attachment>").User?.Id);
    }

    [Fact]
    public void From_node_finds_the_user_for_an_sgid_with_an_invalid_signature()
    {
        var message = RichText.UserSgid(Fixtures.David.Id).Split("--")[0];
        Assert.Equal(Fixtures.David.Id, Resolve($"<action-text-attachment sgid=\"{message}--invalid\"></action-text-attachment>").User?.Id);
    }

    [Fact]
    public void From_node_finds_the_user_for_a_rails_7_sgid()
    {
        var gid = $"gid://campfire/User/{Fixtures.David.Id}";
        var marshaled = new List<byte> { 0x04, 0x08, (byte)'I', (byte)'"', (byte)(gid.Length + 5) };
        marshaled.AddRange(Encoding.ASCII.GetBytes(gid));
        marshaled.AddRange([0x06, (byte)':', 0x06, (byte)'E', (byte)'T']);
        var urlsafe = Convert.ToBase64String([.. marshaled]).Replace('+', '-').Replace('/', '_');
        var payload = $$$"""{"_rails":{"message":"{{{urlsafe}}}","exp":null,"pur":"attachable"}}""";
        var sgid = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "--invalidsignature";

        Assert.Equal(Fixtures.David.Id, Resolve($"<action-text-attachment sgid=\"{sgid}\"></action-text-attachment>").User?.Id);
    }

    [Fact]
    public void From_node_finds_the_user_for_a_rails_7_1_sgid_signed_by_another_secret()
    {
        var payload = $$$"""{"_rails":{"data":"gid://campfire/User/{{{Fixtures.Kevin.Id}}}","pur":"attachable"}}""";
        var sgid = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "--0123456789abcdef";

        Assert.Equal(Fixtures.Kevin.Id, Resolve($"<action-text-attachment sgid=\"{sgid}\"></action-text-attachment>").User?.Id);
    }

    [Fact]
    public void From_node_with_a_nil_sgid_is_missing()
    {
        var resolved = Resolve("<action-text-attachment></action-text-attachment>");
        Assert.Null(resolved.User);
        Assert.Null(resolved.Embed);
    }

    [Fact]
    public void From_node_with_an_invalid_sgid_for_a_non_user_is_missing()
    {
        var payload = $$$"""{"_rails":{"data":"gid://campfire/Rooms::Open/{{{Fixtures.Pets.Id}}}","pur":"attachable"}}""";
        var sgid = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "--invalid";

        Assert.Null(Resolve($"<action-text-attachment sgid=\"{sgid}\"></action-text-attachment>").User);
        Assert.Null(RichText.UserIdFromSgid(sgid));
        Assert.Null(Resolve($"<action-text-attachment sgid=\"{sgid}invalid\"></action-text-attachment>").User);

        // A tampered *user* sgid still resolves: that's the point of the possibly-expired lookup
        Assert.Equal(Fixtures.David.Id, Resolve($"<action-text-attachment sgid=\"{RichText.UserSgid(Fixtures.David.Id)}invalid\"></action-text-attachment>").User?.Id);
    }

    [Fact]
    public void Sgids_look_like_rails_message_verifier_output()
    {
        var sgid = RichText.UserSgid(Fixtures.David.Id);
        var parts = sgid.Split("--");

        Assert.Equal(2, parts.Length);
        Assert.Equal($$$"""{"_rails":{"data":"gid://campfire/User/{{{Fixtures.David.Id}}}","pur":"attachable"}}""", Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])));
        Assert.Matches("^[0-9a-f]{64}$", parts[1]);
        Assert.Equal(sgid, RichText.UserSgid(Fixtures.David.Id));
    }

    // Mentionees (Message::Mentionee)

    [Fact]
    public void Mentioned_user_ids_are_distinct_and_only_count_verified_sgids()
    {
        var forged = RichText.UserSgid(Fixtures.Kevin.Id).Split("--")[0] + "--forged";
        var body = Stored($"<div>Hey {MentionAttachmentFor(Fixtures.David)} {MentionAttachmentFor(Fixtures.David)} {MentionAttachmentFor(Fixtures.Jz)} <action-text-attachment sgid=\"{forged}\"></action-text-attachment></div>");

        Assert.Equal([Fixtures.David.Id, Fixtures.Jz.Id], RichText.MentionedUserIds(body));
        Assert.Empty(RichText.MentionedUserIds("<p>No one</p>"));
    }

    // OpenGraph embeds, stored either way: Trix-era node attributes or Lexxy's content markup

    private IEnumerable<OpengraphEmbed?> EmbedsFrom(string href, string url, string filename = "Title", string caption = "Description", string host = Host) =>
        AttachmentHtml(href, url, filename, caption).Select(html => Resolve(html, host).Embed);

    private static string[] AttachmentHtml(string href, string url, string filename = "Title", string caption = "Description")
    {
        var attributes = $"<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" href=\"{Encode(href)}\" url=\"{Encode(url)}\" filename=\"{Encode(filename)}\" caption=\"{Encode(caption)}\"></action-text-attachment>";
        var content = "<actiontext-opengraph-embed><div class=\"og-embed\"> <div class=\"og-embed__content\"> " +
                      $"<div class=\"og-embed__title\"><a href=\"{Encode(href)}\">{Encode(filename)}</a></div> " +
                      $"<div class=\"og-embed__description\">{Encode(caption)}</div> </div> " +
                      $"<div class=\"og-embed__image\"><img src=\"{Encode(url)}\"></div> </div></actiontext-opengraph-embed>";
        var markup = $"<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" content=\"{Encode(content)}\"></action-text-attachment>";
        return [attributes, markup];
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    [Fact]
    public void Keeps_absolute_http_and_https_links_and_images()
    {
        foreach (var embed in EmbedsFrom("http://example.com/page", "https://example.com/image.png"))
        {
            Assert.Equal("http://example.com/page", embed!.Href);
            Assert.Equal("https://example.com/image.png", embed.Url);
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,pwned")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//example.com/image.png")]
    [InlineData("/rooms/1")]
    [InlineData("rooms/1")]
    [InlineData("")]
    [InlineData("http://exa mple.com/ ")]
    [InlineData("https:/rooms/1")]
    [InlineData("https:rooms/1")]
    [InlineData("http:/rooms/1")]
    [InlineData("https://")]
    [InlineData("http://:80/rooms/1")]
    public void Drops_a_link_and_an_image_that_are_not_web_urls(string value)
    {
        foreach (var embed in EmbedsFrom(value, value))
        {
            Assert.Null(embed!.Href);
            Assert.Null(embed.Url);
        }
    }

    [Theory]
    [InlineData("https://once.campfire.test/rooms/1")]
    [InlineData("http://once.campfire.test/rooms/1")]
    [InlineData("https://ONCE.Campfire.Test/rooms/1")]
    [InlineData("https://once.campfire.test./rooms/1")]
    [InlineData("https://%6fnce.campfire.test/rooms/1")]
    [InlineData("https://%77ww.example.com/x.png")]
    public void Drops_a_link_and_an_image_on_this_campfires_own_host_however_it_is_spelled(string value)
    {
        foreach (var embed in EmbedsFrom(value, value, host: "once.campfire.test"))
        {
            Assert.Null(embed!.Href);
            Assert.Null(embed.Url);
        }

        foreach (var embed in EmbedsFrom("https://example.com/page", "https://example.com/image.png", host: "once.campfire.test:3000"))
        {
            Assert.Equal("https://example.com/page", embed!.Href);
            Assert.Equal("https://example.com/image.png", embed.Url);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1/rooms/1")]
    [InlineData("http://2130706433/rooms/1")]
    [InlineData("http://0177.0.0.1/rooms/1")]
    [InlineData("http://0x7f.0.0.1/rooms/1")]
    [InlineData("http://1.2.3.0xff/rooms/1")]
    [InlineData("http://[::1]/rooms/1")]
    [InlineData("http://localhost/rooms/1")]
    [InlineData("https://203.0.113.10/image.png")]
    public void Drops_a_link_and_an_image_on_a_bare_address_rather_than_a_domain_name(string value)
    {
        foreach (var embed in EmbedsFrom(value, value))
        {
            Assert.Null(embed!.Href);
            Assert.Null(embed.Url);
        }
    }

    [Fact]
    public void Keeps_an_internationalized_domain_written_in_punycode()
    {
        foreach (var embed in EmbedsFrom("https://xn--80aswg.xn--p1ai/page", "https://xn--80aswg.xn--p1ai/image.png"))
        {
            Assert.Equal("https://xn--80aswg.xn--p1ai/page", embed!.Href);
            Assert.Equal("https://xn--80aswg.xn--p1ai/image.png", embed.Url);
        }
    }

    [Fact]
    public void Reads_the_details_out_of_content_markup()
    {
        var embed = Resolve(AttachmentHtml("https://example.com/page", "https://example.com/image.png", "Example title", "Example description")[1]).Embed!;

        Assert.Equal("Example title", embed.Filename);
        Assert.Equal("Example description", embed.Description);
    }

    [Fact]
    public void Renders_the_image_and_the_link_when_both_are_web_urls()
    {
        foreach (var embed in EmbedsFrom("https://example.com/page", "https://example.com/image.png"))
        {
            var html = embed!.Render();
            Assert.Matches("<a rel=\"noreferrer\" target=\"_blank\" href=\"https://example\\.com/page\">Title</a>", html);
            Assert.Matches("<img src=\"https://example\\.com/image\\.png\"", html);
        }
    }

    [Fact]
    public void Renders_no_image_and_no_link_when_neither_is_a_web_url()
    {
        foreach (var embed in EmbedsFrom("javascript:alert(1)", "data:image/svg+xml;base64,PHN2Zy8+"))
        {
            var html = embed!.Render();
            Assert.DoesNotMatch("javascript:", html);
            Assert.DoesNotMatch("data:", html);
            Assert.DoesNotMatch("<img", html);
            Assert.DoesNotMatch("<a ", html);
            Assert.Contains("Title", html);
        }
    }

    [Fact]
    public void Renders_the_title_and_the_description_as_text()
    {
        foreach (var embed in EmbedsFrom("https://example.com/page", "https://example.com/image.png", "<b>Title</b>", "<img src=x onerror=alert(1)>"))
        {
            var html = embed!.Render();
            Assert.DoesNotMatch("<b>", html);
            Assert.DoesNotMatch("<img src=x", html);
            Assert.Contains("&lt;b&gt;Title&lt;/b&gt;", html);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        }
    }

    [Fact]
    public void Truncates_long_titles_and_descriptions_by_characters()
    {
        var embed = new OpengraphEmbed("https://example.com/", null, string.Concat(Enumerable.Repeat("🔥", 300)), new string('a', 600));
        var html = embed.Render();

        Assert.Contains(string.Concat(Enumerable.Repeat("🔥", 279)) + "…</a>", html);
        Assert.Contains(new string('a', 559) + "…</div>", html);
    }

    // editable_body (rich_text_helper_test.rb)

    [Fact]
    public void Editable_body_renders_legacy_opengraph_embeds_into_the_content_attribute()
    {
        var body = """<div>https://example.com/ <action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="https://example.com/image.png" href="https://example.com/" filename="Example title" caption="Example description"></action-text-attachment></div>""";

        var node = Fragment(Editable(body)).QuerySelector("action-text-attachment")!;
        var content = Fragment(node.GetAttribute("content")!);

        Assert.Equal("Example title", content.QuerySelector(".og-embed__title a")!.TextContent.Trim());
        Assert.Equal("https://example.com/", content.QuerySelector(".og-embed__title a")!.GetAttribute("href"));
        Assert.Equal("Example description", content.QuerySelector(".og-embed__description")!.TextContent.Trim());
        Assert.Equal("https://example.com/image.png", content.QuerySelector(".og-embed__image img")!.GetAttribute("src"));
    }

    [Fact]
    public void Editable_body_rebuilds_a_hand_written_embed_from_its_validated_details()
    {
        var content = "<actiontext-opengraph-embed data-controller=\"pwn\" data-action=\"click->pwn#run\"> " +
                      "<div class=\"og-embed\"><div class=\"og-embed__title\"><a href=\"/rooms/1\">Free cookies</a></div> " +
                      "<div class=\"og-embed__image\"><img src=\"/rooms/1/avatar\" data-action=\"load->pwn#run\"></div></div> " +
                      "</actiontext-opengraph-embed>";
        var body = $"<p><action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" url=\"https://example.com/image.png\" content=\"{Encode(content)}\"></action-text-attachment></p>";

        var node = Fragment(Editable(body)).QuerySelector("action-text-attachment")!;
        var rebuilt = Fragment(node.GetAttribute("content")!);

        Assert.Equal("Free cookies", rebuilt.QuerySelector(".og-embed__title")!.TextContent.Trim());
        Assert.DoesNotMatch("rooms/1", node.GetAttribute("content")!);
        Assert.DoesNotMatch("data-", node.GetAttribute("content")!);
        Assert.Null(rebuilt.QuerySelector("a"));
        Assert.Null(rebuilt.QuerySelector("img"));
    }

    [Fact]
    public void Editable_body_restores_the_content_type_of_a_mention_edited_under_trix()
    {
        var body = $"<div>Hey <action-text-attachment sgid=\"{RichText.UserSgid(Fixtures.David.Id)}\" content-type=\"application/octet-stream\"></action-text-attachment></div>";

        var node = Fragment(Editable(body)).QuerySelector("action-text-attachment")!;

        Assert.Equal("application/vnd.campfire.mention", node.GetAttribute("content-type"));
        Assert.Contains("David", node.GetAttribute("content"));
    }

    [Fact]
    public void Editable_body_leaves_bodies_without_attachments_unchanged()
    {
        Assert.Equal(Stored("<p>Plain text</p>"), Editable("<p>Plain text</p>"));
    }

    // Storage

    [Fact]
    public void Canonicalization_keeps_attachment_attributes_and_drops_their_rendered_insides()
    {
        var stored = Stored($"<p>Hey {MentionAttachmentFor(Fixtures.David)}<action-text-attachment sgid=\"x\"><span>rendered</span></action-text-attachment></p>");

        Assert.Contains($"sgid=\"{RichText.UserSgid(Fixtures.David.Id)}\" content-type=\"application/vnd.campfire.mention\" content=\"", stored);
        Assert.Contains("<action-text-attachment sgid=\"x\"></action-text-attachment>", stored);
        Assert.DoesNotContain("<span>rendered</span>", stored);
        Assert.Equal("Hello &lt;world&gt; &amp; you", Stored("Hello <world> & you".Replace("<world>", "&lt;world&gt;")));
    }

    // Rendered body (message.body.to_s, the API's html)

    [Fact]
    public void Rendered_body_keeps_attachments_with_their_partials()
    {
        var stored = Stored($"<p>Hey {MentionAttachmentFor(Fixtures.David)}</p>");
        var html = App.Sql(sql => RichText.RenderBody(sql, stored, Host));

        Assert.StartsWith("<div class=\"lexxy-content\"><p>Hey <action-text-attachment sgid=\"", html);
        Assert.Contains("content-type=\"application/vnd.campfire.mention\"", html);
        Assert.Contains("<span class=\"mention\" sgid=\"", html);
        Assert.Contains(" David</span></action-text-attachment></p></div>", html);
        Assert.DoesNotContain("data-turbo-frame", html);
    }
}
