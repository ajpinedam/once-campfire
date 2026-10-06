using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

namespace Campfire.Web.Views;

/// <summary>
/// Escapes exactly what Rails' <c>ERB::Util.html_escape</c> does (<c>&amp; &lt; &gt; &quot; &#39;</c>)
/// and passes every other character through, so names, emoji and non-Latin text render as
/// themselves rather than as numeric entities. Safe for element content and quoted attributes,
/// which is all templates produce.
/// </summary>
public sealed class MinimalHtmlEncoder : HtmlEncoder
{
    public static readonly MinimalHtmlEncoder Instance = new();

    private static readonly SearchValues<char> Special = SearchValues.Create("&<>\"'");

    private MinimalHtmlEncoder()
    {
    }

    public override int MaxOutputCharactersPerInputCharacter => 6; // "&quot;"

    public override bool WillEncode(int unicodeScalar) => unicodeScalar is '&' or '<' or '>' or '"' or '\'';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength) =>
        new ReadOnlySpan<char>(text, textLength).IndexOfAny(Special);

    public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text) => utf8Text.IndexOfAny("&<>\"'"u8);

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var replacement = unicodeScalar switch
        {
            '&' => "&amp;",
            '<' => "&lt;",
            '>' => "&gt;",
            '"' => "&quot;",
            '\'' => "&#39;",
            _ => null
        };

        if (replacement is null)
        {
            var rune = new Rune(unicodeScalar);
            if (rune.Utf16SequenceLength > bufferLength)
            {
                numberOfCharactersWritten = 0;
                return false;
            }
            numberOfCharactersWritten = rune.EncodeToUtf16(new Span<char>(buffer, bufferLength));
            return true;
        }

        if (replacement.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        replacement.AsSpan().CopyTo(new Span<char>(buffer, bufferLength));
        numberOfCharactersWritten = replacement.Length;
        return true;
    }

    /// <summary>Escapes a string for HTML (allocation-free when nothing needs escaping).</summary>
    public static string Escape(string? value) => value is null ? "" : Instance.Encode(value);
}
