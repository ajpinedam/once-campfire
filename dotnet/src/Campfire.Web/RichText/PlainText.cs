using System.Text;
using AngleSharp.Dom;

namespace Campfire.Web.RichText;

/// <summary>
/// <c>ActionText::PlainTextConversion</c>: reduces a fragment bottom-up into text. Paragraphs and
/// h1 end in a blank line, divs and list items in a newline, <c>br</c> is a newline, list items
/// get "•" or "1." bullets (indented when nested), blockquotes get curly quotes, figcaptions
/// brackets, and script/style vanish. Attachments read as their plain-text representation.
/// </summary>
internal static class PlainText
{
    /// <param name="attachmentText">The plain text an attachment node stands for (Action Text replaces the node with it).</param>
    public static string Convert(IElement root, Func<IElement, string> attachmentText) =>
        RubyText.ChompNewlines(Reduce(root, attachmentText));

    private static string Reduce(INode node, Func<IElement, string> attachmentText)
    {
        if (node is IText text)
        {
            return RubyText.ChompNewlines(text.Data);
        }

        if (node is not IElement element)
        {
            return Join(node, attachmentText);
        }

        switch (element.LocalName)
        {
            case HtmlDom.AttachmentTag:
                // render_attachments swapped the node for a text node holding its representation
                return RubyText.ChompNewlines(attachmentText(element));
            case "script" or "style":
                return "";
            case "h1" or "p":
                return Block(Join(element, attachmentText));
            case "ul" or "ol":
                return BreakIfNestedList(element, Block(Join(element, attachmentText)));
            case "br":
                return "\n";
            case "div":
                return RubyText.ChompNewlines(Join(element, attachmentText)) + "\n";
            case "figcaption":
                return $"[{RubyText.ChompNewlines(Join(element, attachmentText))}]";
            case "blockquote":
                return Blockquote(Block(Join(element, attachmentText)));
            case "li":
                return $"{Indentation(element)}{Bullet(element)} {RubyText.ChompNewlines(Join(element, attachmentText))}\n";
            default:
                return Join(element, attachmentText);
        }
    }

    private static string Join(INode node, Func<IElement, string> attachmentText)
    {
        var children = node.ChildNodes;
        if (children.Length == 0)
        {
            return "";
        }

        if (children.Length == 1)
        {
            return Reduce(children[0], attachmentText);
        }

        var text = new StringBuilder();
        foreach (var child in children)
        {
            text.Append(Reduce(child, attachmentText));
        }
        return text.ToString();
    }

    private static string Block(string childText) => RubyText.ChompNewlines(childText) + "\n\n";

    private static string Blockquote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "“”";
        }

        var first = IndexOfNonSpace(text);
        var last = LastIndexOfNonSpace(text);
        return new StringBuilder(text.Length + 2)
            .Append(text, 0, first).Append('“')
            .Append(text, first, last + 1 - first).Append('”')
            .Append(text, last + 1, text.Length - last - 1)
            .ToString();
    }

    private static string Bullet(IElement item)
    {
        if (NearestListName(item) == "ol" && item.ParentElement is { } parent)
        {
            var index = 0;
            foreach (var sibling in parent.Children)
            {
                if (sibling == item)
                {
                    break;
                }
                index++;
            }
            return $"{index + 1}.";
        }

        return "•";
    }

    private static string Indentation(IElement item)
    {
        var depth = ListDepth(item);
        return depth > 1 ? new string(' ', 2 * (depth - 1)) : "";
    }

    private static string BreakIfNestedList(IElement list, string text) => ListDepth(list) > 0 ? "\n" + text : text;

    private static string? NearestListName(IElement element)
    {
        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (ancestor.LocalName is "ul" or "ol")
            {
                return ancestor.LocalName;
            }
        }
        return null;
    }

    private static int ListDepth(IElement element)
    {
        var depth = 0;
        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (ancestor.LocalName is "ul" or "ol")
            {
                depth++;
            }
        }
        return depth;
    }

    // Ruby's /\S/ (ASCII whitespace only)
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static int IndexOfNonSpace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsSpace(text[i])) return i;
        }
        return 0;
    }

    private static int LastIndexOfNonSpace(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!IsSpace(text[i]))
            {
                return i; // a low surrogate here keeps its pair together: the quote goes after both
            }
        }
        return text.Length - 1;
    }
}
