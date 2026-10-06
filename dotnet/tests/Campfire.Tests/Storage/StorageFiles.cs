using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Storage;
using VipsImage = NetVips.Image;

namespace Campfire.Tests.Storage;

/// <summary>Test inputs: fixture files read from the source tree, and images generated with libvips.</summary>
internal static class StorageFiles
{
    public static string PathOf(string name, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Files", name);

    public static byte[] Read(string name) => File.ReadAllBytes(PathOf(name));

    /// <summary>A gradient image of the given size, encoded as png/jpg/webp/gif/tif.</summary>
    public static byte[] Generated(int width, int height, string format = ".png", int? orientation = null)
    {
        using var gradient = VipsImage.Xyz(width, height);
        using var bands = gradient[0].Bandjoin(gradient[1], gradient[0]);
        using var image = bands.Cast(NetVips.Enums.BandFormat.Uchar);
        if (orientation is { } value)
        {
            using var rotated = image.Mutate(mutable => mutable.Set(NetVips.GValue.GIntType, "orientation", value));
            return rotated.WriteToBuffer(format);
        }
        return image.WriteToBuffer(format);
    }

    public static (int Width, int Height) DimensionsOf(string path)
    {
        using var image = VipsImage.NewFromFile(path);
        return (image.Width, image.Height);
    }

#pragma warning disable CA5351 // Active Storage's integrity checksum is MD5; not used for security
    public static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
#pragma warning restore CA5351

    public static async Task<Blob> StoreAsync(CampfireApp app, byte[] bytes, string filename, string? contentType)
    {
        using var sql = app.Database.Open();
        using var stream = new MemoryStream(bytes);
        return await app.Service<BlobStore>().CreateAsync(sql, stream, filename, contentType, CancellationToken.None);
    }
}
