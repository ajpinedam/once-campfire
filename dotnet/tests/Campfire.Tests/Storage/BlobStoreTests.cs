using System.Text;
using Campfire.Tests.Support;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Storage;

namespace Campfire.Tests.Storage;

public sealed class BlobStoreTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    private BlobStore Store => app.Service<BlobStore>();

    [Fact]
    public void Libvips_is_loaded()
    {
        Assert.True(ImageProcessing.Available);
    }

    [Fact]
    public async Task Stores_uploads_in_the_rails_disk_layout_with_checksum_and_metadata()
    {
        var bytes = StorageFiles.Read("moon.jpg");
        var blob = await StorageFiles.StoreAsync(app, bytes, "moon.jpg", "image/jpeg");

        var expectedPath = Path.Combine(app.StoragePath, "files", blob.Key[..2], blob.Key[2..4], blob.Key);
        Assert.Equal(expectedPath, Store.PathFor(blob.Key));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedPath));
        Assert.Matches("^[0-9a-z]{28}$", blob.Key);
        Assert.Equal(StorageFiles.Md5(bytes), blob.Checksum);
        Assert.Equal(bytes.Length, blob.ByteSize);
        Assert.Equal("image/jpeg", blob.ContentType);
        Assert.Equal(new BlobMetadata(640, 640, null, Analyzed: true), blob.Metadata);

        var stored = app.Sql(sql => Attachments.FindBlob(sql, blob.Id))!;
        Assert.Equal(blob.Metadata, stored.Metadata);
        Assert.Equal("local", app.Sql(sql => sql.ScalarString("SELECT service_name FROM active_storage_blobs WHERE id = @id", ("@id", blob.Id))));
        Assert.Empty(Directory.GetFiles(Path.Combine(app.StoragePath, "files", ".uploads")));
    }

    [Fact]
    public async Task Identifies_content_types_by_their_bytes_rather_than_what_was_declared()
    {
        var png = await StorageFiles.StoreAsync(app, StorageFiles.Generated(10, 10), "notes.txt", "text/plain");
        Assert.Equal("image/png", png.ContentType);

        var html = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes("<!DOCTYPE html><script>alert(1)</script>"), "cat.png", "image/png");
        Assert.Equal("text/html", html.ContentType);

        var svg = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>"), "logo", null);
        Assert.Equal("image/svg+xml", svg.ContentType);

        var text = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes("just some text"), "readme.txt", null);
        Assert.Equal("text/plain", text.ContentType);

        var unknown = await StorageFiles.StoreAsync(app, [0x00, 0x01, 0x02], "mystery", "application/x-custom; charset=binary");
        Assert.Equal("application/x-custom", unknown.ContentType);
    }

    [Fact]
    public async Task Sanitizes_filenames()
    {
        var blob = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes("x"), "../../etc/a:b|c?.txt", null);
        Assert.Equal("a-b-c-.txt", blob.Filename);
        Assert.Equal("passwd", BlobStore.SanitizeFilename("..\\..\\windows\\passwd"));
        Assert.Equal("file", BlobStore.SanitizeFilename("  "));
    }

    [Fact]
    public async Task Rotated_images_report_their_displayed_dimensions()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Generated(300, 100, ".jpg", orientation: 6), "rotated.jpg", "image/jpeg");
        Assert.Equal(100, blob.Metadata.Width);
        Assert.Equal(300, blob.Metadata.Height);

        var thumb = await Store.VariantAsync(blob, VariantKind.MessageThumb, CancellationToken.None);
        Assert.Equal((100, 300), StorageFiles.DimensionsOf(thumb!.Path));
    }

    [Fact]
    public async Task Bitmaps_are_stored_but_never_resized()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Read("pixel.bmp"), "pixel.bmp", "image/bmp");
        Assert.Equal("image/bmp", blob.ContentType);
        Assert.False(BlobStore.IsVariable(blob));
        Assert.Null(blob.Metadata.Width);
        Assert.Null(await Store.VariantAsync(blob, VariantKind.MessageThumb, CancellationToken.None));
    }

    [Theory]
    [InlineData(VariantKind.MessageThumb, 1200, 800, "image/png")]
    [InlineData(VariantKind.AvatarSquare, 512, 341, "image/webp")]
    [InlineData(VariantKind.LogoLarge, 512, 341, "image/png")]
    [InlineData(VariantKind.LogoSmall, 192, 128, "image/png")]
    public async Task Generates_variants_resized_to_limit(VariantKind kind, int width, int height, string contentType)
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Generated(3000, 2000), "big.png", "image/png");

        var variant = await Store.VariantAsync(blob, kind, CancellationToken.None);

        Assert.NotNull(variant);
        Assert.Equal(contentType, variant.ContentType);
        Assert.Equal((width, height), StorageFiles.DimensionsOf(variant.Path));
        Assert.StartsWith(Path.Combine(app.StoragePath, "files", "variants", blob.Key[..2], blob.Key[2..4], blob.Key), variant.Path);
    }

    [Fact]
    public async Task Thumbnails_keep_web_formats_and_never_upscale()
    {
        var jpeg = await StorageFiles.StoreAsync(app, StorageFiles.Read("moon.jpg"), "moon.jpg", "image/jpeg");
        var thumb = await Store.VariantAsync(jpeg, VariantKind.MessageThumb, CancellationToken.None);
        Assert.Equal("image/jpeg", thumb!.ContentType);
        Assert.Equal((640, 640), StorageFiles.DimensionsOf(thumb.Path));

        var tiff = await StorageFiles.StoreAsync(app, StorageFiles.Generated(50, 40, ".tif"), "scan.tif", "image/tiff");
        var converted = await Store.VariantAsync(tiff, VariantKind.MessageThumb, CancellationToken.None);
        Assert.Equal("image/png", converted!.ContentType);
        Assert.Equal((50, 40), StorageFiles.DimensionsOf(converted.Path));
    }

    [Fact]
    public async Task Concurrent_requests_for_a_variant_share_one_generation()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Generated(2400, 1600, ".webp"), "photo.webp", "image/webp");

        var variants = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store.VariantAsync(blob, VariantKind.MessageThumb, CancellationToken.None)));

        Assert.All(variants, variant => Assert.Equal(variants[0]!.Path, variant!.Path));
        Assert.Equal((1200, 800), StorageFiles.DimensionsOf(variants[0]!.Path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(variants[0]!.Path)!));
    }

    [Fact]
    public async Task Videos_without_ffmpeg_are_stored_unanalyzed_and_have_no_preview()
    {
        var mov = new byte[64];
        "ftypqt  "u8.CopyTo(mov.AsSpan(4));
        var blob = await StorageFiles.StoreAsync(app, mov, "clip.mov", "video/quicktime");

        Assert.Equal("video/quicktime", blob.ContentType);
        Assert.True(blob.Metadata.Analyzed);
        if (!VideoProcessing.CanPreview)
        {
            Assert.False(Store.IsPreviewable(blob));
            Assert.Null(await Store.VariantAsync(blob, VariantKind.VideoPreview, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Purge_removes_originals_and_variants()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Generated(800, 600), "gone.png", "image/png");
        var variant = await Store.VariantAsync(blob, VariantKind.AvatarSquare, CancellationToken.None);
        Assert.True(File.Exists(variant!.Path));

        Store.Purge([blob.Key, "missing0000000000000000000000"]);

        Assert.False(File.Exists(Store.PathFor(blob.Key)));
        Assert.False(File.Exists(variant.Path));
    }

    [Fact]
    public void Rejects_keys_that_would_escape_the_storage_directory()
    {
        Assert.Throws<ArgumentException>(() => Store.PathFor("../../etc/passwd"));
    }
}
