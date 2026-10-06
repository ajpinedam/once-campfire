using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Campfire.Web.Configuration;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Security;

namespace Campfire.Web.Storage;

/// <summary>The image variants Campfire uses (Rails' <c>attachable.variant ...</c> declarations).</summary>
public enum VariantKind
{
    /// <summary>User avatars: resize_to_limit 512x512, webp.</summary>
    AvatarSquare,
    /// <summary>Account logo: resize_to_limit 512x512, png.</summary>
    LogoLarge,
    /// <summary>Account logo: resize_to_limit 192x192, png.</summary>
    LogoSmall,
    /// <summary>Message attachments: resize_to_limit 1200x800, original format when web-safe.</summary>
    MessageThumb,
    /// <summary>Video attachments: a poster frame, webp, resize_to_limit 1200x800.</summary>
    VideoPreview
}

/// <summary>
/// Active Storage's Disk service plus analysis and variants. Files live under
/// <c>{FilesPath}/{key[0..2]}/{key[2..4]}/{key}</c> exactly as Rails lays them out, so existing
/// storage directories keep working. Variants are generated lazily (libvips via NetVips;
/// ffmpeg for video posters when available) and cached on disk under
/// <c>{FilesPath}/variants/{key[0..2]}/{key[2..4]}/{key}/</c>.
/// </summary>
public sealed partial class BlobStore : IDisposable
{
    private const int CopyBufferSize = 81920;
    private const int SniffLength = 4096;

    private readonly string _root;
    private readonly string _uploads;
    private readonly string _variants;
    private readonly ILogger<BlobStore> _logger;

    // One generation per variant file at a time; concurrent requests await the same task.
    private readonly ConcurrentDictionary<string, Lazy<Task<VariantFile?>>> _inflight = new(StringComparer.Ordinal);

    // libvips is multithreaded internally; cap how many images are decoded at once to bound memory.
    private readonly SemaphoreSlim _processing = new(Math.Max(1, Environment.ProcessorCount / 2));

    public BlobStore(CampfireSettings settings, KeyRing keys, ILogger<BlobStore> logger)
    {
        _root = settings.FilesPath;
        _uploads = Path.Combine(_root, ".uploads");
        _variants = Path.Combine(_root, "variants");
        _logger = logger;
        Directory.CreateDirectory(_uploads);
        BlobUrls.Configure(keys);

        if (!ImageProcessing.Available)
        {
            logger.LogWarning("libvips could not be loaded; image attachments won't be analyzed or resized");
        }
    }

    /// <summary>
    /// Stores an upload: writes the file, computes the MD5 checksum (base64, as Rails does),
    /// analyzes it (image width/height; video width/height/duration) and inserts the blob row.
    /// </summary>
    public async Task<Blob> CreateAsync(Sql sql, Stream content, string filename, string? contentType, CancellationToken cancellationToken)
    {
        var key = SecureTokens.BlobKey();
        var temporary = Path.Combine(_uploads, $"{key}.upload");
        var path = PathFor(key);
        var head = new byte[SniffLength];
        var headLength = 0;
        long size = 0;
        string checksum;

        try
        {
            using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                    try
                    {
                        int read;
                        while ((read = await content.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)) > 0)
                        {
                            if (headLength < SniffLength)
                            {
                                var take = Math.Min(read, SniffLength - headLength);
                                buffer.AsSpan(0, take).CopyTo(head.AsSpan(headLength));
                                headLength += take;
                            }

                            md5.AppendData(buffer, 0, read);
                            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            size += read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                checksum = Convert.ToBase64String(md5.GetHashAndReset());
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(temporary, path);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        try
        {
            var name = SanitizeFilename(filename);
            var type = ContentTypes.Identify(head.AsSpan(0, headLength), name, contentType);
            var metadata = await AnalyzeAsync(path, type, cancellationToken);
            return Attachments.InsertBlob(sql, key, name, type, metadata, size, checksum);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Absolute path of a blob's original file.</summary>
    public string PathFor(string key)
    {
        EnsureValidKey(key);
        return Path.Combine(_root, key[..2], key[2..4], key);
    }

    /// <summary>Whether a blob can be resized into image variants (Rails' <c>variable?</c>).</summary>
    public static bool IsVariable(Blob blob) =>
        blob.ContentType is { } type && ContentTypes.Variable.Contains(type) && ImageProcessing.Available;

    /// <summary>Whether a blob can have a preview image (Rails' <c>previewable?</c>: videos, when ffmpeg exists).</summary>
    public bool IsPreviewable(Blob blob) => blob.IsVideo && VideoProcessing.CanPreview && ImageProcessing.Available;

    /// <summary>
    /// Path to the requested variant of a blob, generating it first if needed.
    /// Returns null when the blob can't produce that variant.
    /// </summary>
    public async Task<VariantFile?> VariantAsync(Blob blob, VariantKind kind, CancellationToken cancellationToken)
    {
        if (!CanProduce(blob, kind))
        {
            return null;
        }

        var format = FormatFor(blob, kind);
        var path = VariantPathFor(blob.Key, kind, format);
        var variant = new VariantFile(path, MediaType(format));
        if (File.Exists(path))
        {
            return variant;
        }

        var generation = _inflight.GetOrAdd(path, _ => new Lazy<Task<VariantFile?>>(
            () => GenerateOnceAsync(blob, kind, format, variant), LazyThreadSafetyMode.ExecutionAndPublication));
        return await generation.Value.WaitAsync(cancellationToken);
    }

    // Leaves the in-flight map when done (even if every awaiter gave up), so a failure is retried later.
    private async Task<VariantFile?> GenerateOnceAsync(Blob blob, VariantKind kind, ImageFormat format, VariantFile variant)
    {
        try
        {
            return await GenerateAsync(blob, kind, format, variant);
        }
        finally
        {
            _inflight.TryRemove(variant.Path, out _);
        }
    }

    /// <summary>Deletes blob files and their cached variants (after their rows were deleted).</summary>
    public void Purge(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!IsValidKey(key))
            {
                continue;
            }

            TryDelete(PathFor(key));
            try
            {
                var variants = VariantDirectoryFor(key);
                if (Directory.Exists(variants))
                {
                    Directory.Delete(variants, recursive: true);
                }
            }
            catch (IOException exception)
            {
                _logger.LogWarning("Could not delete variants of blob {Key}: {Error}", key, exception.Message);
            }
        }
    }

    public void Dispose() => _processing.Dispose();

    /// <summary>Rails' <c>ActiveStorage::Filename#sanitized</c>: no paths, separators or control characters.</summary>
    public static string SanitizeFilename(string? filename)
    {
        var name = Path.GetFileName((filename ?? "").Replace('\\', '/').TrimEnd('/'));
        name = UnsafeFilenameCharacters().Replace(name.Trim(), "-");
        return name.Length == 0 ? "file" : name;
    }

    private bool CanProduce(Blob blob, VariantKind kind) =>
        kind == VariantKind.VideoPreview ? IsPreviewable(blob) : IsVariable(blob);

    private async Task<VariantFile?> GenerateAsync(Blob blob, VariantKind kind, ImageFormat format, VariantFile variant)
    {
        var original = PathFor(blob.Key);
        if (!File.Exists(original))
        {
            _logger.LogWarning("Blob {Key} has no file on disk; can't generate {Kind}", blob.Key, kind);
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(variant.Path)!);
        var temporary = $"{variant.Path}.{Guid.NewGuid():N}.tmp";
        var frame = kind == VariantKind.VideoPreview ? $"{variant.Path}.{Guid.NewGuid():N}.frame.png" : null;

        await _processing.WaitAsync();
        try
        {
            var source = original;
            if (frame is not null)
            {
                if (!await VideoProcessing.ExtractFrameAsync(original, frame, CancellationToken.None))
                {
                    _logger.LogWarning("ffmpeg could not extract a preview frame from blob {Key}", blob.Key);
                    return null;
                }
                source = frame;
            }

            var (width, height) = LimitFor(kind);
            await Task.Run(() => ImageProcessing.ResizeToLimit(source, temporary, width, height, format));
            File.Move(temporary, variant.Path, overwrite: true);
            return variant;
        }
        catch (Exception exception) when (exception is NetVips.VipsException or IOException or InvalidOperationException)
        {
            _logger.LogWarning("Could not generate {Kind} for blob {Key}: {Error}", kind, blob.Key, exception.Message);
            return null;
        }
        finally
        {
            _processing.Release();
            TryDelete(temporary);
            if (frame is not null)
            {
                TryDelete(frame);
            }
        }
    }

    private static async Task<BlobMetadata> AnalyzeAsync(string path, string contentType, CancellationToken cancellationToken)
    {
        if (ContentTypes.Variable.Contains(contentType))
        {
            var dimensions = await Task.Run(() => ImageProcessing.Dimensions(path), cancellationToken);
            return dimensions is var (width, height)
                ? new BlobMetadata(width, height, null, Analyzed: true)
                : BlobMetadata.Empty with { Analyzed = true };
        }

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return await VideoProcessing.AnalyzeAsync(path, cancellationToken);
        }

        return BlobMetadata.Empty with { Analyzed = true };
    }

    private static (int Width, int Height) LimitFor(VariantKind kind) => kind switch
    {
        VariantKind.AvatarSquare or VariantKind.LogoLarge => (512, 512),
        VariantKind.LogoSmall => (192, 192),
        VariantKind.MessageThumb or VariantKind.VideoPreview => (Message.ThumbnailMaxWidth, Message.ThumbnailMaxHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static ImageFormat FormatFor(Blob blob, VariantKind kind) => kind switch
    {
        VariantKind.AvatarSquare or VariantKind.VideoPreview => ImageFormat.Webp,
        VariantKind.LogoLarge or VariantKind.LogoSmall => ImageFormat.Png,
        VariantKind.MessageThumb => (blob.ContentType ?? "").ToLowerInvariant() switch
        {
            "image/jpeg" or "image/pjpeg" => ImageFormat.Jpeg,
            "image/gif" => ImageFormat.Gif,
            "image/webp" => ImageFormat.Webp,
            _ => ImageFormat.Png
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string MediaType(ImageFormat format) => format switch
    {
        ImageFormat.Png => "image/png",
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Webp => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal static string Extension(ImageFormat format) => ContentTypes.ExtensionFor(MediaType(format));

    private string VariantDirectoryFor(string key) => Path.Combine(_variants, key[..2], key[2..4], key);

    private string VariantPathFor(string key, VariantKind kind, ImageFormat format)
    {
        EnsureValidKey(key);
        return Path.Combine(VariantDirectoryFor(key), $"{BlobUrls.Slug(kind)}.{Extension(format)}");
    }

    // Keys come from the database, but they become file paths: only ever accept the token alphabet.
    private static bool IsValidKey(string? key) => key is { Length: >= 5 } && BlobKeyPattern().IsMatch(key);

    private static void EnsureValidKey(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException("Invalid blob key", nameof(key));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9]+$")]
    private static partial Regex BlobKeyPattern();

    // ActiveStorage::Filename#sanitized: tr("\u{202E}%$|:;/<>?*\"\t\r\n\\", "-")
    [GeneratedRegex("[‮%$|:;/<>?*\"\t\r\n\\\\\\x00-\\x1F]")]
    private static partial Regex UnsafeFilenameCharacters();
}

/// <summary>A generated variant on disk.</summary>
public sealed record VariantFile(string Path, string ContentType);

/// <summary>
/// URLs that serve blobs and variants (Rails' <c>rails_blob_path</c> / <c>url_for(representation)</c>).
/// Blob URLs are signed, permanent and need no session, as in Rails.
/// </summary>
public static class BlobUrls
{
    public const string BlobsPrefix = "/rails/active_storage/blobs";
    public const string RepresentationsPrefix = "/rails/active_storage/representations";
    private const string Purpose = "blob";

    private static SignedIds? _signedIds;

    private static readonly FrozenDictionary<VariantKind, string> Slugs = new Dictionary<VariantKind, string>
    {
        [VariantKind.AvatarSquare] = "square",
        [VariantKind.LogoLarge] = "large",
        [VariantKind.LogoSmall] = "small",
        [VariantKind.MessageThumb] = "thumb",
        [VariantKind.VideoPreview] = "preview"
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, VariantKind> KindsBySlug =
        Slugs.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>Called once at startup (by <see cref="BlobStore"/>'s constructor).</summary>
    public static void Configure(KeyRing keys) => _signedIds = keys.SignedIds;

    private static SignedIds SignedIds => _signedIds ?? throw new InvalidOperationException("BlobUrls.Configure must run at startup");

    /// <summary>The original file; <paramref name="download"/> sets Content-Disposition: attachment.</summary>
    public static string Blob(Blob blob, bool download = false) =>
        $"{BlobsPrefix}/{Token(blob)}/{Uri.EscapeDataString(blob.Filename)}{(download ? "?disposition=attachment" : "")}";

    /// <summary>An image variant of a blob (generated on first request).</summary>
    public static string Representation(Blob blob, VariantKind kind) =>
        $"{RepresentationsPrefix}/{Token(blob)}/{Slug(kind)}/{Uri.EscapeDataString(VariantFilename(blob, kind))}";

    /// <summary>The signed, permanent token standing in for a blob id in URLs.</summary>
    public static string Token(Blob blob) => SignedIds.Generate(blob.Id, Purpose);

    public static long? BlobIdFromToken(string token) => SignedIds.Find(token, Purpose);

    public static string Slug(VariantKind kind) => Slugs[kind];

    public static bool TryParseSlug(string slug, out VariantKind kind) => KindsBySlug.TryGetValue(slug, out kind);

    /// <summary>The original's base name with the variant's extension, e.g. <c>photo.webp</c>.</summary>
    public static string VariantFilename(Blob blob, VariantKind kind)
    {
        var name = Path.GetFileNameWithoutExtension(blob.Filename);
        return $"{(name.Length == 0 ? "file" : name)}.{BlobStore.Extension(BlobStore.FormatFor(blob, kind))}";
    }
}
