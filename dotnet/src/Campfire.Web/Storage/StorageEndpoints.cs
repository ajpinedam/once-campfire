using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Storage;

/// <summary>
/// Active Storage's blob and representation controllers (proxy mode): signed, permanent URLs
/// served straight from disk, with Range support for video and Active Storage's rules for which
/// content may render inline on Campfire's origin.
/// </summary>
public static class StorageEndpoints
{
    // config/initializers/active_storage.rb
    private const string CacheControl = "max-age=3600, public";

    // Anything served from our origin that a browser opens as a document gets no scripts, no
    // subresources and an opaque origin. PDFs are exempt so the browser's viewer still works.
    private const string DocumentPolicy = "default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; sandbox";

    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        string[] methods = [HttpMethods.Get, HttpMethods.Head];

        app.MapMethods(BlobUrls.BlobsPrefix + "/{token}/{*filename}", methods, ServeBlob)
            .AllowUnauthenticated().SkipBrowserCheck();

        app.MapMethods(BlobUrls.RepresentationsPrefix + "/{token}/{variant}/{*filename}", methods, ServeRepresentation)
            .AllowUnauthenticated().SkipBrowserCheck();

        return app;
    }

    private static IResult ServeBlob(HttpContext context, Sql sql, BlobStore store, string token, string? disposition)
    {
        if (FindBlob(sql, token) is not { } blob)
        {
            return Results.NotFound();
        }

        var path = store.PathFor(blob.Key);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        var declared = blob.ContentType ?? ContentTypes.Binary;
        var asBinary = ContentTypes.ServeAsBinary.Contains(declared);
        var inline = !asBinary && ContentTypes.AllowedInline.Contains(declared) &&
                     !string.Equals(disposition, "attachment", StringComparison.OrdinalIgnoreCase);
        var contentType = asBinary ? ContentTypes.Binary : declared;

        SetHeaders(context.Response, inline ? "inline" : "attachment", blob.Filename, contentType);
        return TypedResults.PhysicalFile(path, contentType,
            lastModified: new DateTimeOffset(blob.CreatedAt, TimeSpan.Zero),
            entityTag: new EntityTagHeaderValue($"\"{blob.Key}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> ServeRepresentation(HttpContext context, Sql sql, BlobStore store, string token, string variant)
    {
        if (!BlobUrls.TryParseSlug(variant, out var kind) || FindBlob(sql, token) is not { } blob)
        {
            return Results.NotFound();
        }

        if (await store.VariantAsync(blob, kind, context.RequestAborted) is not { } file || !File.Exists(file.Path))
        {
            return Results.NotFound();
        }

        SetHeaders(context.Response, "inline", BlobUrls.VariantFilename(blob, kind), file.ContentType);
        return TypedResults.PhysicalFile(file.Path, file.ContentType,
            lastModified: new DateTimeOffset(blob.CreatedAt, TimeSpan.Zero),
            entityTag: new EntityTagHeaderValue($"\"{blob.Key}-{BlobUrls.Slug(kind)}\""),
            enableRangeProcessing: true);
    }

    private static Blob? FindBlob(Sql sql, string token) =>
        BlobUrls.BlobIdFromToken(token) is { } id ? Attachments.FindBlob(sql, id) : null;

    private static void SetHeaders(HttpResponse response, string disposition, string filename, string contentType)
    {
        response.Headers.CacheControl = CacheControl;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentDisposition = ContentDisposition(disposition, filename);
        if (!contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.ContentSecurityPolicy = DocumentPolicy;
        }
    }

    /// <summary>
    /// Rails' <c>ActionDispatch::Http::ContentDisposition.format</c>: an ASCII fallback
    /// (transliterated, percent-escaped) plus the exact name as RFC 5987 <c>filename*</c>.
    /// </summary>
    public static string ContentDisposition(string disposition, string filename) =>
        $"{disposition}; filename=\"{PercentEscape(Transliterate(filename), TraditionalSafe)}\"; filename*=UTF-8''{PercentEscape(filename, Rfc5987Safe)}";

    private static bool TraditionalSafe(char c) => char.IsAsciiLetterOrDigit(c) || " !#$+.^_`|~-".Contains(c, StringComparison.Ordinal);

    private static bool Rfc5987Safe(char c) => char.IsAsciiLetterOrDigit(c) || "!#$&+.^_`|~-".Contains(c, StringComparison.Ordinal);

    private static string PercentEscape(string value, Func<char, bool> safe)
    {
        var escaped = new StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < 0x80 && safe(c))
            {
                escaped.Append(c);
                continue;
            }

            var length = char.IsHighSurrogate(c) && i + 1 < value.Length
                ? Encoding.UTF8.GetBytes(value.AsSpan(i++, 2), bytes)
                : Encoding.UTF8.GetBytes(value.AsSpan(i, 1), bytes);
            foreach (var b in bytes[..length])
            {
                escaped.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return escaped.ToString();
    }

    // I18n.transliterate's default approximations for Latin letters; anything else becomes "?".
    // (A table rather than Unicode decomposition: the app runs with invariant globalization.)
    private static readonly FrozenDictionary<char, string> Approximations = BuildApproximations();

    private static string Transliterate(string value)
    {
        var ascii = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < 0x80)
            {
                ascii.Append(c);
            }
            else if (Approximations.TryGetValue(c, out var replacement))
            {
                ascii.Append(replacement);
            }
            else
            {
                ascii.Append('?');
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                }
            }
        }
        return ascii.ToString();
    }

    private static FrozenDictionary<char, string> BuildApproximations()
    {
        var table = new Dictionary<char, string>
        {
            ['Æ'] = "AE", ['æ'] = "ae", ['Œ'] = "OE", ['œ'] = "oe", ['ß'] = "ss", ['Ð'] = "D", ['ð'] = "d",
            ['Þ'] = "TH", ['þ'] = "th", ['Ø'] = "O", ['ø'] = "o", ['Ł'] = "L", ['ł'] = "l", ['Đ'] = "D", ['đ'] = "d", ['×'] = "x"
        };

        void Map(string letters, char ascii)
        {
            foreach (var letter in letters)
            {
                table[letter] = ascii.ToString();
            }
        }

        Map("ÀÁÂÃÄÅĀĂĄǍ", 'A'); Map("àáâãäåāăąǎ", 'a');
        Map("ÇĆĈĊČ", 'C'); Map("çćĉċč", 'c');
        Map("ĎḌ", 'D'); Map("ďḍ", 'd');
        Map("ÈÉÊËĒĔĖĘĚ", 'E'); Map("èéêëēĕėęě", 'e');
        Map("ĜĞĠĢ", 'G'); Map("ĝğġģ", 'g');
        Map("ĤĦ", 'H'); Map("ĥħ", 'h');
        Map("ÌÍÎÏĨĪĬĮİǏ", 'I'); Map("ìíîïĩīĭįıǐ", 'i');
        Map("Ĵ", 'J'); Map("ĵ", 'j');
        Map("Ķ", 'K'); Map("ķ", 'k');
        Map("ĹĻĽĿ", 'L'); Map("ĺļľŀ", 'l');
        Map("ÑŃŅŇ", 'N'); Map("ñńņňŉ", 'n');
        Map("ÒÓÔÕÖŌŎŐǑ", 'O'); Map("òóôõöōŏőǒ", 'o');
        Map("ŔŖŘ", 'R'); Map("ŕŗř", 'r');
        Map("ŚŜŞŠȘ", 'S'); Map("śŝşšș", 's');
        Map("ŢŤŦȚ", 'T'); Map("ţťŧț", 't');
        Map("ÙÚÛÜŨŪŬŮŰŲǓǕǗǙǛ", 'U'); Map("ùúûüũūŭůűųǔǖǘǚǜ", 'u');
        Map("Ŵ", 'W'); Map("ŵ", 'w');
        Map("ÝŶŸ", 'Y'); Map("ýÿŷ", 'y');
        Map("ŹŻŽ", 'Z'); Map("źżž", 'z');
        return table.ToFrozenDictionary();
    }
}
