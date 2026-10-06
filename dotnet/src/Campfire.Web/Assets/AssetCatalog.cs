using System.Collections.Frozen;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace Campfire.Web.Assets;

/// <summary>An asset loaded into memory, with a Brotli-compressed copy for text types.</summary>
public sealed record AssetFile(string LogicalPath, string DigestedPath, string ContentType, byte[] Content, byte[]? Brotli, string ETag);

/// <summary>
/// Propshaft's job: every file under wwwroot/assets is served at
/// <c>/assets/{name}-{digest}.{ext}</c> with immutable caching, CSS <c>url(...)</c> references are
/// rewritten to digested paths, and an importmap maps module names to digested JS.
/// Built once at startup and immutable afterwards.
/// </summary>
public sealed partial class AssetCatalog
{
    public const string Prefix = "/assets/";

    private readonly FrozenDictionary<string, AssetFile> _byLogicalPath;
    private readonly FrozenDictionary<string, AssetFile> _byDigestedPath;

    private AssetCatalog(IEnumerable<AssetFile> files)
    {
        var list = files.ToList();
        _byLogicalPath = list.ToFrozenDictionary(file => file.LogicalPath, StringComparer.Ordinal);
        _byDigestedPath = list.ToFrozenDictionary(file => file.DigestedPath, StringComparer.Ordinal);
        Stylesheets = StylesheetOrder(list.Select(file => file.LogicalPath));
        ImportMap = new ImportMap(this);
    }

    /// <summary>Process-wide instance used by view helpers; assigned once at startup.</summary>
    public static AssetCatalog Current { get; private set; } = null!;

    /// <summary><c>stylesheet_link_tag :all</c>: Lexxy's styles first, so the app's overrides win.</summary>
    public IReadOnlyList<string> Stylesheets { get; }

    public ImportMap ImportMap { get; }

    public static AssetCatalog Load(string assetsRoot)
    {
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        contentTypes.Mappings[".mjs"] = "text/javascript";
        contentTypes.Mappings[".js"] = "text/javascript";

        var raw = Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories)
            .Select(path => (Logical: Path.GetRelativePath(assetsRoot, path).Replace('\\', '/'), Path: path))
            .Where(entry => !Path.GetFileName(entry.Logical).StartsWith('.'))
            .ToDictionary(entry => entry.Logical, entry => File.ReadAllBytes(entry.Path), StringComparer.Ordinal);

        // Digest non-CSS first, so CSS can reference the digested names of what it points at.
        var digested = raw.Where(pair => !pair.Key.EndsWith(".css", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => DigestedName(pair.Key, pair.Value), StringComparer.Ordinal);

        var files = new List<AssetFile>(raw.Count);
        foreach (var (logical, bytes) in raw)
        {
            var content = logical.EndsWith(".css", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(RewriteCssUrls(logical, Encoding.UTF8.GetString(bytes), raw, digested))
                : bytes;

            var digestedPath = DigestedName(logical, content);
            digested[logical] = digestedPath;
            var contentType = contentTypes.TryGetContentType(logical, out var type) ? type : "application/octet-stream";
            files.Add(new AssetFile(logical, digestedPath, contentType, content, Compress(contentType, content), $"\"{digestedPath}\""));
        }

        return Current = new AssetCatalog(files);
    }

    public bool TryGetByDigestedPath(string path, out AssetFile file) => _byDigestedPath.TryGetValue(path, out file!);

    public bool TryGetByLogicalPath(string path, out AssetFile file) => _byLogicalPath.TryGetValue(path, out file!);

    /// <summary>Rails' <c>asset_path</c>: <c>/assets/check-1a2b3c4d.svg</c> for <c>check.svg</c>.</summary>
    public string PathFor(string logicalPath) =>
        _byLogicalPath.TryGetValue(logicalPath, out var file)
            ? Prefix + file.DigestedPath
            : throw new ArgumentException($"Asset not found: {logicalPath}", nameof(logicalPath));

    public IEnumerable<string> LogicalPaths => _byLogicalPath.Keys;

    private static string DigestedName(string logicalPath, byte[] content)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(content))[..8];
        var extensionStart = logicalPath.LastIndexOf('.');
        var slash = logicalPath.LastIndexOf('/');
        return extensionStart > slash + 1
            ? $"{logicalPath[..extensionStart]}-{digest}{logicalPath[extensionStart..]}"
            : $"{logicalPath}-{digest}";
    }

    private static string RewriteCssUrls(string cssPath, string css, Dictionary<string, byte[]> all, Dictionary<string, string> digested)
    {
        var directory = cssPath.Contains('/') ? cssPath[..cssPath.LastIndexOf('/')] + "/" : "";
        return CssUrl().Replace(css, match =>
        {
            var url = match.Groups["url"].Value;
            if (url.StartsWith("data:", StringComparison.Ordinal) || url.StartsWith("http", StringComparison.Ordinal) ||
                url.StartsWith('#') || url.StartsWith("//", StringComparison.Ordinal))
            {
                return match.Value;
            }

            var logical = url.StartsWith('/') ? url.TrimStart('/') : directory + url;
            if (logical.StartsWith("assets/", StringComparison.Ordinal))
            {
                logical = logical["assets/".Length..];
            }

            if (digested.TryGetValue(logical, out var target) || (all.ContainsKey(logical) && (target = logical) is not null))
            {
                return $"url(\"{Prefix}{target}\")";
            }
            return match.Value;
        });
    }

    private static byte[]? Compress(string contentType, byte[] content)
    {
        var compressible = contentType.StartsWith("text/", StringComparison.Ordinal) ||
                           contentType.Contains("javascript", StringComparison.Ordinal) ||
                           contentType.Contains("json", StringComparison.Ordinal) ||
                           contentType.Contains("svg", StringComparison.Ordinal);
        if (!compressible || content.Length < 512)
        {
            return null;
        }

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(content);
        }
        return output.Length < content.Length ? output.ToArray() : null;
    }

    private static List<string> StylesheetOrder(IEnumerable<string> logicalPaths)
    {
        string[] lexxy = ["lexxy-variables.css", "lexxy-content.css", "lexxy-editor.css"];
        var css = logicalPaths.Where(path => path.EndsWith(".css", StringComparison.Ordinal) && !path.Contains('/')).ToHashSet(StringComparer.Ordinal);
        return [.. lexxy.Where(css.Contains), .. css.Except(lexxy).Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex("""url\(\s*['"]?(?<url>[^'")]+)['"]?\s*\)""")]
    private static partial Regex CssUrl();
}
