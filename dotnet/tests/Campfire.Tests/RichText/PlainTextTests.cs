using Campfire.Tests.Support;
using Campfire.Web.RichText;

namespace Campfire.Tests.RichText;

/// <summary>ActionText::PlainTextConversion as Campfire uses it (search index, pushes, webhooks, sounds).</summary>
public sealed class PlainTextTests(CampfireApp app) : RichTextTestBase(app), IClassFixture<CampfireApp>
{
    [Theory]
    [InlineData("Hello back!", "Hello back!")]
    [InlineData("<span>My hovercraft is full of eels</span>", "My hovercraft is full of eels")]
    [InlineData("<p>One</p><p>Two</p>", "One\n\nTwo")]
    [InlineData("<div>One</div><div>Two</div>", "One\nTwo")]
    [InlineData("<div>One<br>Two</div>", "One\nTwo")]
    [InlineData("<h1>Title</h1><p>Body</p>", "Title\n\nBody")]
    [InlineData("<ul><li>one</li><li>two</li></ul>", "• one\n• two")]
    [InlineData("<ol><li>first</li><li>second</li></ol>", "1. first\n2. second")]
    [InlineData("<ul><li>one<ul><li>nested</li></ul></li></ul>", "• one\n  • nested")]
    [InlineData("<blockquote>quoted</blockquote>", "“quoted”")]
    [InlineData("<blockquote> </blockquote>", "“”")]
    [InlineData("<pre>line 1\nline 2</pre>", "line 1\nline 2")]
    [InlineData("<p>Hi<script>alert(1)</script><style>p{}</style></p>", "Hi")]
    [InlineData("<figure>img<figcaption>A cat</figcaption></figure>", "img[A cat]")]
    [InlineData("🔥\nmultiple lines\n💯", "🔥\nmultiple lines\n💯")]
    [InlineData("Fish &amp; chips &lt;3", "Fish & chips <3")]
    [InlineData("<p>trailing</p>\n\n", "trailing")]
    public void Converts_html_to_plain_text(string body, string expected)
    {
        Assert.Equal(expected, PlainText(body));
    }

    [Fact]
    public void Mentions_read_as_at_names_and_link_previews_as_nothing()
    {
        var body = $"<p>Hey {MentionAttachmentFor(Fixtures.David)}, look</p>" +
                   "<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" href=\"https://example.com/\" filename=\"Example\" caption=\"Caption\"></action-text-attachment>";

        Assert.Equal("Hey @David, look", PlainText(body));
    }

    [Fact]
    public void Missing_attachables_read_as_their_caption()
    {
        Assert.Equal("Before caption after", PlainText("Before <action-text-attachment caption=\"caption\"></action-text-attachment> after"));
    }

    [Theory]
    [InlineData("😄🤘", true)]
    [InlineData("🔥", true)]
    [InlineData("❤️", true)]
    [InlineData("👍🏽", true)]
    [InlineData("🇺🇸", true)]
    [InlineData("Haha! 😄🤘", false)]
    [InlineData("🔥\nmultiple lines\n💯", false)]
    [InlineData("🔥 💯", false)]
    [InlineData("👨‍👩‍👧", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void All_emoji_matches_rubys_regex(string text, bool expected)
    {
        Assert.Equal(expected, RichTextService.IsAllEmoji(text));
    }
}
