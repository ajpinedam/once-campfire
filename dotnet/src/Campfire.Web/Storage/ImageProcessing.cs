using NetVips;
using VipsImage = NetVips.Image;

namespace Campfire.Web.Storage;

/// <summary>Output encodings for variants.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    Gif,
    Webp
}

/// <summary>
/// libvips via NetVips: header-only analysis and thumbnailing (Rails' image_processing gem with
/// the vips backend). Synchronous and CPU-bound — callers run it off the request thread.
/// </summary>
public static class ImageProcessing
{
    private static readonly Lazy<bool> Initialized = new(Initialize);

    /// <summary>Whether libvips loaded; without it nothing is variable and analysis is skipped.</summary>
    public static bool Available => Initialized.Value;

    /// <summary>
    /// config/initializers/vips.rb: refuse libvips operations that aren't fuzzed against untrusted
    /// input, and the OpenSlide loader explicitly. Also keeps libvips' operation cache small, since
    /// every request decodes a different image.
    /// </summary>
    private static bool Initialize()
    {
        try
        {
            if (!ModuleInitializer.VipsInitialized)
            {
                return false;
            }

            NetVips.NetVips.BlockUntrusted = true;
            Operation.Block("VipsForeignLoadOpenslide", true);
            Cache.Max = 50;
            Cache.MaxFiles = 0;
            Cache.MaxMem = 32 * 1024 * 1024;
            return true;
        }
        catch (Exception exception) when (exception is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException or VipsException)
        {
            return false;
        }
    }

    /// <summary>
    /// Width and height as displayed. Like Rails' Vips analyzer, EXIF orientations 5–8 (rotated a
    /// quarter turn) swap the stored dimensions. Null when the file can't be read as an image.
    /// </summary>
    public static (int Width, int Height)? Dimensions(string path)
    {
        if (!Available)
        {
            return null;
        }

        try
        {
            using var image = VipsImage.NewFromFile(path, access: Enums.Access.Sequential);
            var orientation = image.Contains("orientation") && image.Get("orientation") is int value ? value : 1;
            return orientation is >= 5 and <= 8 ? (image.Height, image.Width) : (image.Width, image.Height);
        }
        catch (VipsException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>resize_to_limit [width, height]</c>: fits the image inside the box without ever enlarging
    /// it, applying EXIF rotation, and writes it in <paramref name="format"/>. Metadata other than
    /// the colour profile is stripped (no GPS coordinates leak through thumbnails).
    /// Animated GIF/WebP keep their frames when the output format supports animation.
    /// </summary>
    public static void ResizeToLimit(string source, string destination, int width, int height, ImageFormat format)
    {
        if (!Available)
        {
            throw new InvalidOperationException("libvips is not available");
        }

        var animated = format is ImageFormat.Gif or ImageFormat.Webp;
        VipsImage image;
        try
        {
            image = VipsImage.Thumbnail(animated ? $"{source}[n=-1]" : source, width, height: height, size: Enums.Size.Down);
        }
        catch (VipsException) when (animated)
        {
            image = VipsImage.Thumbnail(source, width, height: height, size: Enums.Size.Down);
        }

        using (image)
        {
            const Enums.ForeignKeep keep = Enums.ForeignKeep.Icc;
            switch (format)
            {
                case ImageFormat.Png:
                    image.Pngsave(destination, keep: keep);
                    break;
                case ImageFormat.Jpeg:
                    image.Jpegsave(destination, keep: keep);
                    break;
                case ImageFormat.Gif:
                    image.Gifsave(destination, keep: keep);
                    break;
                case ImageFormat.Webp:
                    image.Webpsave(destination, keep: keep);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format));
            }
        }
    }
}
