using System.Text.RegularExpressions;
using Campfire.Tests.Support;

namespace Campfire.Tests.RichText;

/// <summary>Port of test/helpers/content_filters_test.rb and messages_helper_test.rb.</summary>
public sealed class ContentFiltersTests(CampfireApp app) : RichTextTestBase(app), IClassFixture<CampfireApp>
{
    private const string BasecampEmbed =
        "<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" url=\"https://basecamp.com/assets/general/opengraph.png\" href=\"https://basecamp.com/\" filename=\"Project management software, online collaboration\" caption=\"Trusted by millions, Basecamp puts everything you need to get work done in one place.\" content=\"<actiontext-opengraph-embed>\n      <div class=&quot;og-embed&quot;>\n        <div class=&quot;og-embed__content&quot;>\n          <div class=&quot;og-embed__title&quot;>Project management software, online collaboration</div>\n        </div>\n      </div>\n    </actiontext-opengraph-embed>\"></action-text-attachment>\n";

    private const string TwitterEmbed =
        "<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" url=\"https://pbs.twimg.com/ext_tw_video_thumb/1752476502791503873/pu/img/WEAqUgarUxWjPNHD.jpg\" href=\"https://twitter.com/dhh/status/1752476663303323939\" filename=\"DHH (@dhh)\" caption=\"We're playing with adding easy extension points to ONCE/Campfire.\" content=\"&lt;actiontext-opengraph-embed&gt;&lt;div class=&quot;og-embed&quot;&gt;&lt;/div&gt;&lt;/actiontext-opengraph-embed&gt;\"><figure class=\"attachment attachment--content attachment--og\"><div class=\"og-embed gap\"><div class=\"og-embed__title\"><a href=\"https://twitter.com/dhh/status/1752476663303323939\">DHH (@dhh)</a></div></div></figure></action-text-attachment>\n";

    [Fact]
    public void Entire_message_contains_an_unfurled_url()
    {
        var body = $"<div>https://basecamp.com/{BasecampEmbed}</div>";

        var filtered = Filtered(body);
        Assert.NotEqual(Stored(body), filtered);
        Assert.Matches("<div><action-text-attachment", filtered);
    }

    [Fact]
    public void Entire_message_contains_an_unfurled_url_in_a_lexxy_body()
    {
        var body = $"<p><a href=\"https://basecamp.com/\">https://basecamp.com/</a></p>{BasecampEmbed}";

        var filtered = Filtered(body);
        Assert.DoesNotMatch(@">\s*https://basecamp\.com/\s*</a>", filtered);
        Assert.Matches("<action-text-attachment", filtered);
    }

    [Fact]
    public void Message_includes_additional_text_besides_an_unfurled_url()
    {
        var body = $"<div>Hello https://basecamp.com/{BasecampEmbed}</div>";

        var filtered = Filtered(body);
        Assert.Equal(Stored(body), filtered);
        Assert.Matches(@"<div>Hello https://basecamp\.com/<action-text-attachment", filtered);
    }

    [Fact]
    public void Unfurled_tweet_with_an_avatar_image_gets_the_twitter_avatar_treatment()
    {
        var body = """<div>https://twitter.com/37signals/status/1750290547908952568<action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="https://pbs.twimg.com/profile_images/1671940407633010689/9P5gi6LF_200x200.jpg" href="https://twitter.com/37signals/status/1750290547908952568" filename="37signals (@37signals)" caption="We're back up on all apps, everyone."></action-text-attachment></div>""";

        Assert.Matches("og-embed--twitter-avatar", Presentation(body));
    }

    [Fact]
    public void Unfurled_tweet_with_an_avatar_image_in_a_lexxy_body_gets_the_twitter_avatar_treatment()
    {
        var content = """<actiontext-opengraph-embed><div class="og-embed gap"><div class="og-embed__content"><div class="og-embed__title"><a href="https://twitter.com/x/status/1">Tweet</a></div><div class="og-embed__description">desc</div></div><div class="og-embed__image"><img src="https://pbs.twimg.com/profile_images/x.jpg" class="image center" alt="" /></div></div></actiontext-opengraph-embed>""";
        var body = $"""<p><a href="https://twitter.com/x/status/1">https://twitter.com/x/status/1</a></p><action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" content="{System.Net.WebUtility.HtmlEncode(content)}"></action-text-attachment>""";

        Assert.Matches("og-embed--twitter-avatar", Presentation(body));
    }

    [Fact]
    public void Unfurled_tweet_with_a_content_image_is_not_styled_as_an_avatar()
    {
        var body = """<div>https://twitter.com/dhh/status/1748445489648050505<action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="https://pbs.twimg.com/media/GEO5l04bsAA9f6H.jpg" href="https://twitter.com/dhh/status/1748445489648050505" filename="DHH (@dhh)" caption="We pay homage to the glorious MIT License!"></action-text-attachment></div>""";

        Assert.DoesNotMatch("og-embed--twitter-avatar", Presentation(body));
    }

    [Theory]
    [InlineData("https://x.com/dhh/status/1752476663303323939")]
    [InlineData("https://x.com/dhh/status/1752476663303323939?s=20")]
    public void Entire_message_contains_an_unfurled_url_from_x_com_that_unfurls_to_twitter_com(string text)
    {
        var body = $"<div>{text}{TwitterEmbed}</div>";

        var filtered = Filtered(body);
        Assert.NotEqual(Stored(body), filtered);
        Assert.Matches("<div><action-text-attachment", filtered);
    }

    [Fact]
    public void Message_keeps_strikethrough_underline_and_code_block_formatting()
    {
        var html = Presentation("""<p>Hello <s>struck</s> <u>under</u> <mark>marked</mark></p><pre data-language="ruby">def x<br>end</pre>""");

        Assert.Matches("<s>struck</s>", html);
        Assert.Matches("<u>under</u>", html);
        Assert.Matches("<mark>marked</mark>", html);
        Assert.Matches("<pre data-language=\"ruby\">", html);
    }

    [Fact]
    public void Message_contains_a_forbidden_tag()
    {
        Assert.Equal("Hello World", Filtered("""Hello <img src="https://ssecurityrise.com/tests/billionlaughs-cache.svg">World"""));
    }

    [Fact]
    public void Message_with_a_link_using_an_unsafe_uri_scheme()
    {
        var filtered = Filtered("""<div><a href="javascript:alert(1)">x</a></div>""");
        Assert.DoesNotMatch("javascript:", filtered);
        Assert.Matches("<a>x</a>", filtered);
    }

    [Fact]
    public void Message_with_an_event_handler_attribute_on_an_allowed_tag()
    {
        var filtered = Filtered("""<div><a href="/x" onmouseover="alert(1)">x</a> <span onclick="alert(2)">y</span></div>""");
        Assert.DoesNotMatch("onmouseover", filtered);
        Assert.DoesNotMatch("onclick", filtered);
        Assert.Matches("<a href=\"/x\">x</a>", filtered);
        Assert.Matches("<span>y</span>", filtered);
    }

    [Fact]
    public void Message_with_a_data_uri_link()
    {
        var filtered = Filtered("""<div><a href="data:text/html,pwned">x</a></div>""");
        Assert.DoesNotMatch("data:", filtered);
        Assert.Matches("<a>x</a>", filtered);
    }

    [Fact]
    public void Message_with_a_safe_link_and_formatting_is_preserved()
    {
        var filtered = Filtered("""<div><a href="https://example.com">example</a> <strong>bold</strong> <code>code</code><ul><li>one</li><li>two</li></ul></div>""");
        Assert.Matches("<a href=\"https://example\\.com\">example</a>", filtered);
        Assert.Matches("<strong>bold</strong>", filtered);
        Assert.Matches("<code>code</code>", filtered);
        Assert.Matches(@"<ul>\s*<li>one</li>\s*<li>two</li>\s*</ul>", filtered);
    }

    [Fact]
    public void Sanitizing_attributes_neutralizes_unsafe_input_and_preserves_benign_content()
    {
        var body = "<div><a href=\"javascript:alert(1)\" onclick=\"x()\">link</a> <a href=\"data:text/html,pwned\">data</a> " +
                   "<span class=\"cf-twitter-avatar\" onmouseover=\"y()\">avatar</span> " +
                   $"<img src=\"https://evil.example/x.svg\"> Hey {MentionAttachmentFor(Fixtures.David)}</div>";

        var filtered = Filtered(body);

        Assert.DoesNotMatch("javascript:", filtered);
        Assert.DoesNotMatch("data:text/html", filtered);
        Assert.DoesNotMatch("onclick", filtered);
        Assert.DoesNotMatch("onmouseover", filtered);
        Assert.DoesNotMatch(@"evil\.example", filtered);
        Assert.Matches("<span class=\"cf-twitter-avatar\">avatar</span>", filtered);
        Assert.Matches(">link<", filtered);
        Assert.Contains($"<action-text-attachment sgid=\"{RichText.UserSgid(Fixtures.David.Id)}\"", filtered);
    }

    [Fact]
    public void Message_with_formatting_saved_under_trix_renders_unchanged()
    {
        var body = "<div>Hello <strong>bold</strong> <em>it</em> <del>gone</del> <a href=\"https://example.com/\">link</a><br>second line</div><h1>Heading</h1><blockquote>quoted</blockquote><pre>line 1\nline 2</pre><ul><li>one</li></ul><ol><li>first</li></ol>";

        Assert.Equal(body, Filtered(body));
        Assert.Contains(body, Presentation(body));
    }

    [Fact]
    public void Message_with_a_table_keeps_the_table()
    {
        var body = "<figure class=\"lexxy-content__table-wrapper\"><table><tbody><tr><th><p>Name</p></th></tr><tr><td><p>Jason</p></td></tr></tbody></table></figure>";

        Assert.Equal(body, Filtered(body));
        Assert.Matches(new Regex("<table>.*<th><p>Name</p></th>.*<td><p>Jason</p></td>", RegexOptions.Singleline), Presentation(body));
    }

    [Fact]
    public void Message_with_a_mention_attachment()
    {
        var filtered = Filtered($"<div>Hey {MentionAttachmentFor(Fixtures.David)}</div>");
        var expected = new Regex($"<action-text-attachment sgid=\"{Regex.Escape(RichText.UserSgid(Fixtures.David.Id))}\" content-type=\"application/vnd\\.campfire\\.mention\" content=\"(.*?)\"></action-text-attachment>", RegexOptions.Singleline);

        Assert.Matches(expected, filtered);
    }

    // messages_helper_test.rb

    [Fact]
    public void Presentation_neutralizes_unsafe_uri_schemes_in_links()
    {
        var presentation = Presentation("""<div><a href="javascript:alert(1)">x</a></div>""");
        Assert.DoesNotMatch("javascript:", presentation);
        Assert.Matches("<a>x</a>", presentation);
    }

    [Fact]
    public void Presentation_strips_event_handler_attributes_from_allowed_tags()
    {
        var presentation = Presentation("""<div><a href="/x" onmouseover="alert(1)">x</a></div>""");
        Assert.DoesNotMatch("onmouseover", presentation);
        Assert.Matches("<a href=\"/x\">x</a>", presentation);
    }

    [Fact]
    public void Presentation_preserves_safe_links_and_formatting()
    {
        var presentation = Presentation("""<div><a href="https://example.com">example</a> <strong>bold</strong></div>""");
        Assert.Matches("<a href=\"https://example\\.com\"[^>]*>example</a>", presentation);
        Assert.Matches("<strong>bold</strong>", presentation);
    }

    // Presentation shape

    [Fact]
    public void Presentation_is_wrapped_in_the_lexxy_content_layout()
    {
        Assert.Equal("<div class=\"lexxy-content\"><p>Hi</p></div>", Presentation("<p>Hi</p>"));
        Assert.Equal("<div class=\"lexxy-content\">Hello bot</div>", Presentation("Hello bot"));
    }

    [Fact]
    public void Presentation_renders_mentions_with_the_users_avatar_and_name()
    {
        var presentation = Presentation($"<p>Hey {MentionAttachmentFor(Fixtures.David)}</p>");

        Assert.Matches(new Regex("<span class=\"mention\"><a title=\"David\" class=\"btn avatar\" href=\"/users/\\d+\"><img width=\"48\" height=\"48\" src=\"/users/[^\"]+/avatar\\?v=\\d+\"></a> David</span>"), presentation);
        Assert.DoesNotMatch("action-text-attachment", presentation);
        Assert.DoesNotMatch("sgid", presentation);
    }

    [Fact]
    public void Presentation_renders_an_unfurled_link_preview()
    {
        var presentation = Presentation($"<div>Look https://basecamp.com/{BasecampEmbed}</div>");

        Assert.Contains("<div class=\"og-embed__title\">", presentation);
        Assert.Contains("<a href=\"https://basecamp.com/\">Project management software, online collaboration</a>", presentation);
        Assert.Contains("<img src=\"https://basecamp.com/assets/general/opengraph.png\" class=\"image center\" alt=\"\">", presentation);
        Assert.DoesNotMatch("<figure", presentation);
    }

    // XSS

    [Theory]
    [InlineData("<script>alert(1)</script>Hi", "alert")]
    [InlineData("<style>body { display: none }</style>Hi", "display")]
    [InlineData("<img src=x onerror=alert(1)>Hi", "onerror")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>Hi", "iframe")]
    [InlineData("<svg><a href=\"javascript:alert(1)\">x</a></svg>Hi", "javascript")]
    [InlineData("<a href=\"java&#x09;script:alert(1)\">x</a>Hi", "script:")]
    [InlineData("<a href=\"JAVASCRIPT:alert(1)\">x</a>Hi", "JAVASCRIPT")]
    [InlineData("<form action=\"https://evil.example\"><input name=x></form>Hi", "form")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">Hi</div>", "style")]
    [InlineData("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>Hi", "javascript")]
    public void Presentation_never_lets_markup_execute(string body, string forbidden)
    {
        var presentation = Presentation(body);
        Assert.DoesNotContain(forbidden, presentation, StringComparison.Ordinal);
        Assert.Contains("Hi", presentation);
    }

    [Fact]
    public void Presentation_drops_embeds_aimed_at_this_campfire()
    {
        var body = $"""<div>hi<action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="https://{Host}/rooms/1/avatar" href="https://{Host}/rooms/1" filename="Free cookies" caption="Click"></action-text-attachment></div>""";

        var presentation = Presentation(body);
        Assert.Contains("Free cookies", presentation);
        Assert.DoesNotContain($"https://{Host}", presentation);
        Assert.DoesNotContain("<img", presentation);
    }

    [Fact]
    public void Presentation_drops_embeds_with_escaped_or_numeric_hosts()
    {
        foreach (var url in new[] { "https://%63ampfire.test/x", "http://2130706433/x", "http://0x7f.0.0.1/x", "http://127.0.0.1/x" })
        {
            var body = $"""<div>hi<action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="{url}" href="{url}" filename="T" caption="D"></action-text-attachment></div>""";
            var presentation = Presentation(body);
            Assert.DoesNotContain(url, presentation);
            Assert.DoesNotContain("<img", presentation);
        }
    }

    [Fact]
    public void Content_attachments_and_remote_images_are_not_rendered()
    {
        var body = """<div>hi<action-text-attachment content-type="text/html" content="&lt;img src=x onerror=alert(1)&gt;"></action-text-attachment><action-text-attachment content-type="image/png" url="https://evil.example/x.png"></action-text-attachment></div>""";

        var presentation = Presentation(body);
        Assert.DoesNotContain("onerror", presentation);
        Assert.DoesNotContain("evil.example", presentation);
        Assert.Contains("hi", presentation);
    }

    // Auto-linking

    [Fact]
    public void Bare_urls_and_email_addresses_become_links()
    {
        var presentation = Presentation("<p>See https://example.com/a?b=1&amp;c=2, www.example.org or mail jz@37signals.com.</p>");

        Assert.Contains("<a target=\"_blank\" href=\"https://example.com/a?b=1&amp;c=2\">https://example.com/a?b=1&amp;c=2</a>,", presentation);
        Assert.Contains("<a target=\"_blank\" href=\"http://www.example.org\">www.example.org</a>", presentation);
        Assert.Contains("<a target=\"_blank\" href=\"mailto:jz@37signals.com\">jz@37signals.com</a>.", presentation);
    }

    [Fact]
    public void Trailing_punctuation_and_balanced_brackets_are_handled_like_rails_autolink()
    {
        var presentation = Presentation("<p>(see https://en.wikipedia.org/wiki/Fire_(disambiguation)) and https://example.com/x!</p>");

        Assert.Contains("(see <a target=\"_blank\" href=\"https://en.wikipedia.org/wiki/Fire_(disambiguation)\">https://en.wikipedia.org/wiki/Fire_(disambiguation)</a>)", presentation);
        Assert.Contains("<a target=\"_blank\" href=\"https://example.com/x\">https://example.com/x</a>!", presentation);
    }

    [Fact]
    public void Existing_links_and_attribute_values_are_not_linked_again()
    {
        var presentation = Presentation("<p><a href=\"https://example.com/\">https://example.com/</a> <abbr title=\"https://example.com/t\">t</abbr></p>");

        Assert.Equal(1, Regex.Count(presentation, "<a "));
        Assert.Contains("<abbr title=\"https://example.com/t\">t</abbr>", presentation);
    }
}
