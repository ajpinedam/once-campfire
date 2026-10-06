using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Campfire.Tests.Support;
using Campfire.Web.Storage;

namespace Campfire.Tests.Storage;

public sealed class BlobServingTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    [Fact]
    public async Task Serves_images_inline_with_caching_and_safety_headers()
    {
        var bytes = StorageFiles.Read("moon.jpg");
        var blob = await StorageFiles.StoreAsync(app, bytes, "moon.jpg", "image/jpeg");
        using var client = app.Anonymous();

        var response = await client.GetAsync(BlobUrls.Blob(blob), accept: "image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("inline; filename=\"moon.jpg\"; filename*=UTF-8''moon.jpg", response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl.MaxAge);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
    }

    [Fact]
    public async Task Answers_conditional_and_range_requests()
    {
        var bytes = StorageFiles.Read("moon.jpg");
        var blob = await StorageFiles.StoreAsync(app, bytes, "moon.jpg", "image/jpeg");
        using var client = app.Anonymous();

        var first = await client.GetAsync(BlobUrls.Blob(blob), accept: "*/*");
        var conditional = new HttpRequestMessage(HttpMethod.Get, BlobUrls.Blob(blob));
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.Http.SendAsync(conditional)).StatusCode);

        var ranged = new HttpRequestMessage(HttpMethod.Get, BlobUrls.Blob(blob));
        ranged.Headers.Range = new RangeHeaderValue(0, 9);
        var partial = await client.Http.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(bytes[..10], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Downloads_as_attachments_when_asked()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Read("moon.jpg"), "moon.jpg", "image/jpeg");
        using var client = app.Anonymous();

        var response = await client.GetAsync(BlobUrls.Blob(blob, download: true), accept: "*/*");

        Assert.StartsWith("attachment;", response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "drawing.svg")]
    [InlineData("<html><body><script>alert(1)</script></body></html>", "page.html")]
    public async Task Serves_active_content_as_opaque_downloads(string content, string filename)
    {
        var blob = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes(content), filename, null);
        using var client = app.Anonymous();

        var response = await client.GetAsync(BlobUrls.Blob(blob), accept: "*/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment;", response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Types_not_allowed_inline_are_downloaded()
    {
        var blob = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes("hello"), "notes.txt", "text/plain");
        using var client = app.Anonymous();

        var response = await client.GetAsync(BlobUrls.Blob(blob), accept: "*/*");

        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment;", response.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task Encodes_unicode_filenames_for_both_header_forms()
    {
        Assert.Equal(
            "attachment; filename=\"resume %3F.pdf\"; filename*=UTF-8''r%C3%A9sum%C3%A9%20%F0%9F%94%A5.pdf",
            StorageEndpoints.ContentDisposition("attachment", "résumé 🔥.pdf"));
        Assert.Equal(
            "inline; filename=\"a%22b.png\"; filename*=UTF-8''a%22b.png",
            StorageEndpoints.ContentDisposition("inline", "a\"b.png"));

        var blob = await StorageFiles.StoreAsync(app, Encoding.UTF8.GetBytes("%PDF-1.4"), "résumé.pdf", null);
        using var client = app.Anonymous();
        var response = await client.GetAsync(BlobUrls.Blob(blob), accept: "*/*");
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline;", response.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task Serves_representations_generated_on_first_request()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Generated(1000, 1000), "square.png", "image/png");
        using var client = app.Anonymous();

        var url = BlobUrls.Representation(blob, VariantKind.AvatarSquare);
        Assert.EndsWith("/square/square.webp", url);
        var response = await client.GetAsync(url, accept: "image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline;", response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Equal("RIFF", Encoding.ASCII.GetString((await response.Content.ReadAsByteArrayAsync())[..4]));
    }

    [Fact]
    public async Task Rejects_tampered_tokens_unknown_variants_and_unvariable_blobs()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Read("moon.jpg"), "moon.jpg", "image/jpeg");
        var bitmap = await StorageFiles.StoreAsync(app, StorageFiles.Read("pixel.bmp"), "pixel.bmp", "image/bmp");
        using var client = app.Anonymous();

        var token = BlobUrls.Token(blob);
        var forged = token.Replace(blob.Id + "--", (blob.Id + 1) + "--", StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BlobUrls.BlobsPrefix}/{forged}/moon.jpg", "*/*")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BlobUrls.BlobsPrefix}/{token}x/moon.jpg", "*/*")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BlobUrls.RepresentationsPrefix}/{token}/huge/moon.jpg", "*/*")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BlobUrls.Representation(bitmap, VariantKind.MessageThumb), "*/*")).StatusCode);
    }

    [Fact]
    public async Task Purged_blobs_are_gone()
    {
        var blob = await StorageFiles.StoreAsync(app, StorageFiles.Read("moon.jpg"), "moon.jpg", "image/jpeg");
        app.Service<BlobStore>().Purge([blob.Key]);
        using var client = app.Anonymous();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BlobUrls.Blob(blob), "*/*")).StatusCode);
    }
}
