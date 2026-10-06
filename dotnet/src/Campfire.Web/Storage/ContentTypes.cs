using System.Collections.Frozen;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;

namespace Campfire.Web.Storage;

/// <summary>
/// Content-type rules from Active Storage: identifying uploads by their bytes (Marcel's job in
/// Rails), which types libvips may resize, and which types are safe to serve inline.
/// </summary>
public static class ContentTypes
{
    public const string Binary = "application/octet-stream";

    /// <summary>
    /// Rails' default <c>variable_content_types</c> minus bmp, ico and psd, which
    /// config/initializers/vips.rb removes (their libvips loaders aren't fuzzed).
    /// </summary>
    public static readonly FrozenSet<string> Variable = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "image/png", "image/gif", "image/jpeg", "image/pjpeg", "image/tiff", "image/webp", "image/avif", "image/heic", "image/heif");

    /// <summary>Formats a resized message thumbnail keeps; anything else becomes PNG.</summary>
    public static readonly FrozenSet<string> WebImage = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "image/png", "image/jpeg", "image/gif", "image/webp");

    /// <summary>
    /// Active Storage's <c>content_types_to_serve_as_binary</c> (plus scripts): a browser would execute
    /// or render these as documents on our origin, so they're always downloaded as opaque bytes.
    /// </summary>
    public static readonly FrozenSet<string> ServeAsBinary = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "text/html", "image/svg+xml", "application/postscript", "application/x-shockwave-flash", "text/xml",
        "application/xml", "application/xhtml+xml", "application/mathml+xml", "text/cache-manifest",
        "text/javascript", "application/javascript", "application/x-javascript", "application/ecmascript", "text/ecmascript");

    /// <summary>Active Storage's <c>content_types_allowed_inline</c>; everything else is served as an attachment.</summary>
    public static readonly FrozenSet<string> AllowedInline = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "image/webp", "image/avif", "image/png", "image/gif", "image/jpeg", "image/tiff", "image/bmp",
        "image/vnd.adobe.photoshop", "image/vnd.microsoft.icon", "application/pdf");

    private static readonly FileExtensionContentTypeProvider Extensions = CreateExtensionProvider();

    // ZIP containers whose real type only the file name tells (Marcel prefers the name for these).
    private static readonly FrozenSet<string> ZipBasedExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".key", ".pages", ".numbers", ".sketch", ".xpi");

    /// <summary>
    /// The content type of an upload: what its first bytes say, then what its name says, then what
    /// the client declared — the order Marcel uses for <c>ActiveStorage::Blob#identify</c>.
    /// </summary>
    public static string Identify(ReadOnlySpan<byte> head, string filename, string? declared)
    {
        var byExtension = Extensions.TryGetContentType(filename, out var extensionType) ? extensionType : null;
        var sniffed = Sniff(head);

        if (sniffed == "application/zip" && ZipBasedExtensions.Contains(Path.GetExtension(filename)) && byExtension is not null)
        {
            return byExtension;
        }

        return sniffed ?? byExtension ?? Normalize(declared) ?? Binary;
    }

    /// <summary>The media type without parameters, lowercased; null when blank.</summary>
    public static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var type = (separator >= 0 ? contentType[..separator] : contentType).Trim().ToLowerInvariant();
        return type.Contains('/', StringComparison.Ordinal) ? type : null;
    }

    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/pjpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "bin"
    };

    private static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (Starts(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
        if (Starts(head, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return "image/gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8))
        {
            var format = head.Slice(8, 4);
            if (format.SequenceEqual("WEBP"u8)) return "image/webp";
            if (format.SequenceEqual("WAVE"u8)) return "audio/x-wav";
            if (format.SequenceEqual("AVI "u8)) return "video/x-msvideo";
        }
        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8)) return "image/tiff";
        if (head.StartsWith("BM"u8) && head.Length >= 14) return "image/bmp";
        if (Starts(head, 0x00, 0x00, 0x01, 0x00)) return "image/vnd.microsoft.icon";
        if (head.StartsWith("8BPS"u8)) return "image/vnd.adobe.photoshop";
        if (head.StartsWith("%PDF-"u8)) return "application/pdf";
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8)) return IsoMediaType(head.Slice(8, 4));
        if (head.Length >= 8 && (head.Slice(4, 4).SequenceEqual("moov"u8) || head.Slice(4, 4).SequenceEqual("mdat"u8))) return "video/quicktime";
        if (Starts(head, 0x1A, 0x45, 0xDF, 0xA3)) return head.IndexOf("webm"u8) >= 0 ? "video/webm" : "video/x-matroska";
        if (head.StartsWith("ID3"u8) || Starts(head, 0xFF, 0xFB) || Starts(head, 0xFF, 0xF3) || Starts(head, 0xFF, 0xF2)) return "audio/mpeg";
        if (head.StartsWith("OggS"u8)) return head.IndexOf("theora"u8) >= 0 ? "video/ogg" : "audio/ogg";
        if (head.StartsWith("fLaC"u8)) return "audio/flac";
        if (head.StartsWith("PK\x03\x04"u8)) return "application/zip";
        if (Starts(head, 0x1F, 0x8B)) return "application/gzip";
        if (Starts(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "application/x-7z-compressed";
        if (head.StartsWith("Rar!\x1A\x07"u8)) return "application/x-rar-compressed";
        return SniffMarkup(head);
    }

    private static bool Starts(ReadOnlySpan<byte> head, params ReadOnlySpan<byte> magic) => head.StartsWith(magic);

    private static string IsoMediaType(ReadOnlySpan<byte> brand) => Encoding.ASCII.GetString(brand) switch
    {
        "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "hevm" or "hevs" => "image/heic",
        "mif1" or "msf1" => "image/heif",
        "avif" or "avis" => "image/avif",
        "qt  " => "video/quicktime",
        "M4A " or "M4B " => "audio/mp4",
        var b when b.StartsWith("3g", StringComparison.Ordinal) => "video/3gpp",
        _ => "video/mp4"
    };

    // Documents a browser would render: SVG, HTML and XML, recognized however they're named.
    private static string? SniffMarkup(ReadOnlySpan<byte> head)
    {
        var text = Encoding.UTF8.GetString(Starts(head, 0xEF, 0xBB, 0xBF) ? head[3..] : head).TrimStart();
        if (text.Length == 0 || text[0] != '<')
        {
            return null;
        }

        if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<svg", StringComparison.OrdinalIgnoreCase) && text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return "image/svg+xml";
        }

        foreach (var tag in (string[])["<!doctype html", "<html", "<head", "<body", "<script", "<iframe", "<!--"])
        {
            if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return "text/html";
            }
        }

        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : null;
    }

    private static FileExtensionContentTypeProvider CreateExtensionProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".heic"] = "image/heic";
        provider.Mappings[".heif"] = "image/heif";
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".webp"] = "image/webp";
        provider.Mappings[".mov"] = "video/quicktime";
        provider.Mappings[".md"] = "text/markdown";
        return provider;
    }
}
