using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.Web.Views;

namespace Campfire.Web.RichText;

/// <summary>
/// rails_autolink's <c>auto_link(html, html: { target: "_blank" })</c> over already-sanitized HTML:
/// bare URLs and email addresses become links, unless they sit inside a tag or an existing
/// <c>&lt;a&gt;</c>. Works on the HTML string with regexes, exactly as the gem does.
/// </summary>
internal static partial class AutoLink
{
    private static readonly Dictionary<char, char> Brackets = new() { [']'] = '[', [')'] = '(', ['}'] = '{' };

    public static string Apply(string html) => LinkEmailAddresses(LinkUrls(html));

    private static string LinkUrls(string text)
    {
        StringBuilder? output = null;
        var copied = 0;

        foreach (Match match in UrlPattern().Matches(text))
        {
            if (IsAutoLinked(text, match.Index, match.Index + match.Length))
            {
                continue;
            }

            output ??= new StringBuilder(text.Length + 64);
            output.Append(text, copied, match.Index - copied);
            copied = match.Index + match.Length;

            var href = match.Value;
            var punctuation = new List<string>();

            // don't include trailing punctuation character as part of the URL
            while (href.Length > 0)
            {
                var (last, width) = LastRune(href);
                if (IsUrlEndCharacter(last))
                {
                    break;
                }

                var removed = href[^width..];
                href = href[..^width];
                punctuation.Add(removed);

                if (removed.Length == 1 && Brackets.TryGetValue(removed[0], out var opening) && Count(href, opening) > Count(href, removed[0]))
                {
                    href += removed;
                    punctuation.RemoveAt(punctuation.Count - 1);
                    break;
                }
            }

            // don't include trailing &gt; entities as part of the URL
            var trailingGt = "";
            if (href.EndsWith("&gt;", StringComparison.Ordinal))
            {
                href = href[..^4];
                trailingGt = "&gt;";
            }

            var linkText = href;
            if (!match.Groups["scheme"].Success)
            {
                href = "http://" + href;
            }

            output.Append("<a target=\"_blank\" href=\"").Append(href.Replace("\"", "&quot;", StringComparison.Ordinal)).Append("\">")
                .Append(linkText).Append("</a>");
            for (var i = punctuation.Count - 1; i >= 0; i--)
            {
                output.Append(punctuation[i]);
            }
            output.Append(trailingGt);
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    private static string LinkEmailAddresses(string text)
    {
        StringBuilder? output = null;
        var copied = 0;

        foreach (Match match in EmailPattern().Matches(text))
        {
            if (IsAutoLinked(text, match.Index, match.Index + match.Length))
            {
                continue;
            }

            output ??= new StringBuilder(text.Length + 64);
            output.Append(text, copied, match.Index - copied);
            copied = match.Index + match.Length;

            // mail_to(email, email, target: "_blank")
            output.Append("<a target=\"_blank\" href=\"mailto:").Append(UrlEncode(match.Value).Replace("%40", "@", StringComparison.Ordinal))
                .Append("\">").Append(MinimalHtmlEncoder.Escape(match.Value)).Append("</a>");
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// The gem's <c>auto_linked?(left, right)</c>: the match is inside a tag (an unclosed "&lt;" before
    /// it and a "&gt;" after), or after an <c>&lt;a ...&gt;</c> that hasn't been closed yet.
    /// </summary>
    private static bool IsAutoLinked(string text, int start, int end)
    {
        var left = text.AsSpan(0, start);
        if (OpenTag().IsMatch(left) && text.IndexOf('>', end) >= 0)
        {
            return true;
        }

        // left.rindex(/<a\b.*?>/i) and $' !~ /<\/a>/i
        for (var i = start - 2; i >= 0; i--)
        {
            if (text[i] != '<' || (text[i + 1] | 0x20) != 'a')
            {
                continue;
            }

            var opening = AnchorOpening().Match(text, i, start - i);
            if (opening.Success && opening.Index == i)
            {
                var after = text.AsSpan(i + opening.Length, start - i - opening.Length);
                return !after.Contains("</a>", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static (Rune Rune, int Width) LastRune(string text) =>
        Rune.DecodeLastFromUtf16(text, out var rune, out var width) == System.Buffers.OperationStatus.Done ? (rune, width) : (Rune.ReplacementChar, 1);

    // [\p{Word}\/\-=;] — Ruby's \p{Word}: letters, marks, numbers and connector punctuation
    private static bool IsUrlEndCharacter(Rune rune)
    {
        if (rune.Value is '/' or '-' or '=' or ';')
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
            UnicodeCategory.ConnectorPunctuation => true,
            _ => false
        };
    }

    private static int Count(string text, char c)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == c) count++;
        }
        return count;
    }

    /// <summary>Ruby's <c>ERB::Util.url_encode</c>: everything but <c>[a-zA-Z0-9_\-.~]</c> is percent-encoded.</summary>
    private static string UrlEncode(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-' or (byte)'.' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    // AUTO_LINK_RE (Ruby's \s is ASCII whitespace)
    [GeneratedRegex(@"(?:(?<scheme>(?:ed2k|ftp|http|https|irc|mailto|news|gopher|nntp|telnet|webcal|xmpp|callto|feed|svn|urn|aim|rsync|tag|ssh|sftp|rtsp|afs|file):)//|www\.)[^ \t\r\n\f\v< ""]+", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex UrlPattern();

    // AUTO_EMAIL_RE (Ruby's \w is ASCII)
    [GeneratedRegex(@"(?<![A-Za-z0-9_.!#$%&'*/=?^`{|}~+-])[A-Za-z0-9_.!#$%+-]\.?[A-Za-z0-9_.!#$%&'*/=?^`{|}~+-]*@[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+")]
    private static partial Regex EmailPattern();

    // AUTO_LINK_CRE[0]: /<[^>]+$/ — Ruby's $ is end of line
    [GeneratedRegex(@"<[^>]+$", RegexOptions.Multiline)]
    private static partial Regex OpenTag();

    // AUTO_LINK_CRE[2]: /<a\b.*?>/i — Ruby's . doesn't cross newlines
    [GeneratedRegex(@"\G<a\b.*?>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorOpening();
}
