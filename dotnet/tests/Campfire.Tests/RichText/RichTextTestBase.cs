using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.RichText;

namespace Campfire.Tests.RichText;

/// <summary>Shared helpers mirroring test/test_helpers/mention_test_helper.rb.</summary>
public abstract class RichTextTestBase(CampfireApp app)
{
    protected const string Host = "campfire.test";

    protected CampfireApp App => app;
    protected Fixtures Fixtures => app.Fixtures;
    protected RichTextService RichText => app.Service<RichTextService>();

    /// <summary>What a message stores for a submitted body (Message.create! canonicalizes it).</summary>
    protected string Stored(string body) => RichText.Canonicalize(body);

    protected string Filtered(string body) => app.Sql(sql => RichText.ApplyPresentationFilters(sql, Stored(body), Host));

    protected string Presentation(string body) => app.Sql(sql => RichText.RenderPresentation(sql, Stored(body), Host));

    protected string PlainText(string body) => app.Sql(sql => RichText.ToPlainText(sql, Stored(body)));

    protected string Editable(string body) => app.Sql(sql => RichText.EditableBody(sql, Stored(body), Host));

    /// <summary><c>mention_attachment_for(:user)</c>.</summary>
    protected string MentionAttachmentFor(User user) =>
        $"<action-text-attachment sgid=\"{RichText.UserSgid(user.Id)}\" content-type=\"application/vnd.campfire.mention\" content=\"{RichText.RenderMention(user).Replace("\"", "&quot;")}\"></action-text-attachment>";

    protected static AngleSharp.Dom.IElement Fragment(string html)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(string.Empty);
        document.Body!.InnerHtml = html;
        return document.Body;
    }
}
