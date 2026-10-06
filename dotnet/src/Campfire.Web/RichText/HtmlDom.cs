using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Campfire.Web.RichText;

/// <summary>
/// HTML fragment parsing and serialization (Action Text's <c>ActionText::Fragment</c>). A fragment
/// is parsed in a fresh document's body, which then serves as the fragment root: its children are
/// the fragment, and serializing its inner HTML gives the fragment back (HTML5 serialization rules,
/// like Nokogiri's HTML5 serializer).
/// </summary>
internal static class HtmlDom
{
    // HtmlParser carries a browsing context; one per thread keeps parsing lock-free.
    private static readonly ThreadLocal<HtmlParser> Parser = new(() => new HtmlParser(new HtmlParserOptions
    {
        IsKeepingSourceReferences = false,
        IsScripting = false
    }));

    // A context document per thread: each fragment gets a fresh, detached <body> owned by it.
    // Building a whole HtmlDocument per parse cost a URL parse and trips through AngleSharp's global
    // StringBuilderPool lock, which serialized concurrent requests (~10% of posting's CPU).
    // Callers use a fragment synchronously on the thread that parsed it.
    [ThreadStatic]
    private static AngleSharp.Html.Dom.IHtmlDocument? _context;

    public const string AttachmentTag = "action-text-attachment";

    /// <summary>Parses <paramref name="html"/> as a body fragment; the returned element is the fragment root.</summary>
    public static IElement Parse(string? html)
    {
        var document = _context ??= Parser.Value!.ParseDocument(string.Empty);
        var root = document.CreateElement("body");
        if (!string.IsNullOrEmpty(html))
        {
            root.InnerHtml = html;
        }
        return root;
    }

    public static string Serialize(IElement root) => root.InnerHtml;

    /// <summary>Every <c>&lt;action-text-attachment&gt;</c> under <paramref name="root"/>, in document order.</summary>
    public static IElement[] Attachments(IElement root) => [.. root.QuerySelectorAll(AttachmentTag)];

    /// <summary>Replaces a node's children with parsed HTML (Nokogiri's <c>inner_html=</c>).</summary>
    public static void SetInnerHtml(IElement element, string html) => element.InnerHtml = html;

    /// <summary>Moves a node's children before it and removes it (rails-html-sanitizer's "strip").</summary>
    public static void Unwrap(INode node)
    {
        var parent = node.Parent;
        if (parent is null)
        {
            return;
        }

        foreach (var child in node.ChildNodes.ToArray())
        {
            parent.InsertBefore(child, node);
        }
        parent.RemoveChild(node);
    }

    /// <summary>Removes a node with everything inside it.</summary>
    public static void Remove(INode node) => node.Parent?.RemoveChild(node);

    /// <summary>Replaces a node with a text node.</summary>
    public static void ReplaceWithText(INode node, string text)
    {
        var parent = node.Parent;
        if (parent is null)
        {
            return;
        }

        if (text.Length > 0)
        {
            parent.InsertBefore(node.Owner!.CreateTextNode(text), node);
        }
        parent.RemoveChild(node);
    }

    /// <summary>A copy of an element with the same attributes (in order) and no children.</summary>
    public static IElement ShallowCopy(IElement element, IReadOnlySet<string>? keepAttributes = null)
    {
        var copy = element.Owner!.CreateElement(element.LocalName);
        foreach (var attribute in element.Attributes)
        {
            if (keepAttributes is null || keepAttributes.Contains(attribute.Name))
            {
                copy.SetAttribute(attribute.Name, attribute.Value);
            }
        }
        return copy;
    }
}
