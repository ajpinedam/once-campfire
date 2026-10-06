using System.Collections.Concurrent;
using System.Net;
using Campfire.Tests.Integrations.Support;
using Campfire.Web.Net;
using Campfire.Web.OpenGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Tests.Integrations;

/// <summary>Ports of test/models/opengraph/{document,location,fetch,metadata}_test.rb.</summary>
public sealed class OpenGraphTests : IAsyncLifetime
{
    private const string PublicIp = "93.184.216.34";

    private readonly FakeHostResolver _dns = new() { Default = [IPAddress.Parse(PublicIp)] };
    private readonly ConcurrentQueue<IPEndPoint> _dialed = new();
    private StubWeb _web = null!;
    private PinnedHttpClient _http = null!;
    private OpenGraphFetch _fetch = null!;
    private OpenGraphLocations _locations = null!;
    private OpenGraphUnfurler _unfurler = null!;

    public async Task InitializeAsync()
    {
        _web = await StubWeb.StartAsync();
        var guard = new PrivateNetworkGuard(_dns);
        _http = new PinnedHttpClient(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), int.MaxValue, _web.Dialer(_dialed));
        _fetch = new OpenGraphFetch(guard, _http);
        _locations = new OpenGraphLocations(guard, _fetch, NullLogger<OpenGraphLocations>.Instance);
        _unfurler = new OpenGraphUnfurler(_locations);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _web.DisposeAsync();
    }

    // Document

    [Theory]
    [InlineData("property")]
    [InlineData("name")]
    public void Extracts_opengraph_tags(string attribute)
    {
        var attributes = OpenGraphDocument.OpenGraphAttributes(
            $"""<html><head><meta {attribute}="og:url" content="https://example.com"><meta {attribute}="og:title" content="Hey!"><meta {attribute}="og:description" content="desc.."><meta {attribute}="og:image" content="https://example.com/image.png"></head></html>""");

        Assert.Equal("https://example.com", attributes["url"]);
        Assert.Equal("Hey!", attributes["title"]);
        Assert.Equal("desc..", attributes["description"]);
        Assert.Equal("https://example.com/image.png", attributes["image"]);
    }

    [Fact]
    public void Drops_non_ascii_when_the_document_declares_no_encoding()
    {
        var attributes = OpenGraphDocument.OpenGraphAttributes(
            "<html><head><meta name=\"og:url\" content=\"https://example.com\"><meta name=\"og:title\" content=\"Hey!\"><meta name=\"og:description\" content=\"Hello â\u0080\u0099World\"></head></html>");

        Assert.Equal("Hey!", attributes["title"]);
        Assert.Equal("Hello World", attributes["description"]);
    }

    [Fact]
    public void Keeps_unicode_when_the_document_declares_its_encoding()
    {
        var attributes = OpenGraphDocument.OpenGraphAttributes(
            "<html><head><meta charset=\"utf-8\"><meta property=\"og:title\" content=\"Café ☕\"></head></html>");

        Assert.Equal("Café ☕", attributes["title"]);
    }

    // Location

    [Theory]
    [InlineData("https://www.example.com", true)]
    [InlineData("http://www.example.com", true)]
    [InlineData("~/etc/password", false)]
    [InlineData("ftp://speedtest.tele2.net", false)]
    [InlineData("httpfake", false)]
    [InlineData(" foo", false)]
    [InlineData("https/incorrect", false)]
    public async Task Validates_urls(string url, bool valid) =>
        Assert.Equal(valid, (await _locations.CheckAsync(url)).IsValid);

    [Theory]
    [InlineData("172.16.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:c0a8:0101")]
    public async Task Urls_on_private_networks_are_not_public(string address)
    {
        _dns.Answer("metadata.internal", address);
        var location = await _locations.CheckAsync("https://metadata.internal");

        Assert.False(location.IsValid);
        Assert.Equal(["is not public"], location.Errors);
    }

    [Theory]
    [InlineData("http://www.example.com/video.mp4")]
    [InlineData("http://www.example.com/archive.tar")]
    [InlineData("http://www.example.com/large.heic")]
    [InlineData("http://www.example.com/image.jpeg")]
    [InlineData("http://www.example.com/malware.exe")]
    [InlineData("http://www.example.com/massiveOS.iso")]
    public async Task Avoids_reading_files_and_media_when_expecting_html(string url)
    {
        _web.Stub("GET", url, StubResponse.Html("<body>ok</body>"));
        Assert.Null(await _locations.ReadHtmlAsync(url));
        Assert.Empty(_web.Requests);
    }

    // Fetch

    [Fact]
    public async Task Fetches_valid_html_connecting_to_the_resolved_address()
    {
        _web.Stub("GET", "http://www.example.com/", StubResponse.Html("<body>ok<body>"));

        Assert.Equal("<body>ok<body>", (await _fetch.FetchDocumentAsync(new Uri("http://www.example.com")))?.Html);
        Assert.Equal(new IPEndPoint(IPAddress.Parse(PublicIp), 80), Assert.Single(_dialed));
    }

    [Fact]
    public async Task Discards_other_content_types()
    {
        _web.Stub("GET", "http://www.example.com/", new StubResponse(200, "text/plain", "I'm not HTML!"u8.ToArray()));
        Assert.Null(await _fetch.FetchDocumentAsync(new Uri("http://www.example.com")));
    }

    [Fact]
    public async Task Follows_redirects_resolving_each_hop()
    {
        _dns.Answer("www.other.com", "1.2.3.4");
        _web.Stub("GET", "http://www.example.com/", StubResponse.Redirect("http://www.other.com/"));
        _web.Stub("GET", "http://www.other.com/", StubResponse.Html("<body>ok<body>"));

        Assert.Equal("<body>ok<body>", (await _fetch.FetchDocumentAsync(new Uri("http://www.example.com")))?.Html);
        Assert.Equal([IPAddress.Parse(PublicIp), IPAddress.Parse("1.2.3.4")], _dialed.Select(endpoint => endpoint.Address).ToArray());
    }

    [Fact]
    public async Task Does_not_follow_redirects_to_private_networks()
    {
        _dns.Answer("www.other.com", "127.0.0.1");
        _web.Stub("GET", "http://www.example.com/", StubResponse.Redirect("http://www.other.com/"));
        _web.Stub("GET", "http://www.other.com/", StubResponse.Html("<body>ok<body>"));

        await Assert.ThrowsAsync<PrivateNetworkViolationException>(() => _fetch.FetchDocumentAsync(new Uri("http://www.example.com"), IPAddress.Parse("1.2.3.4")));
        Assert.DoesNotContain(_web.Requests, request => request.Url.Contains("www.other.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_not_follow_relative_or_non_http_redirects()
    {
        _web.Stub("GET", "http://www.example.com/", StubResponse.Redirect("file:///etc/passwd"));
        await Assert.ThrowsAsync<RedirectDeniedException>(() => _fetch.FetchDocumentAsync(new Uri("http://www.example.com")));
    }

    [Fact]
    public async Task Gives_up_on_redirects_that_never_finish()
    {
        _web.Stub("GET", "http://www.example.com/", StubResponse.Redirect("http://www.example.com/"));
        await Assert.ThrowsAsync<TooManyRedirectsException>(() => _fetch.FetchDocumentAsync(new Uri("http://www.example.com")));
        Assert.Equal(OpenGraphFetch.MaxRedirects, _web.Requests.Count);
    }

    [Fact]
    public async Task Ignores_large_responses_by_content_length()
    {
        _web.Stub("GET", "http://www.example.com/", new StubResponse(200, "text/html", "too large"u8.ToArray(), ContentLength: 1L << 30));
        Assert.Null(await _fetch.FetchDocumentAsync(new Uri("http://www.example.com")));
    }

    [Fact]
    public async Task Ignores_large_responses_missing_their_content_length()
    {
        _web.Stub("GET", "http://www.example.com/", new StubResponse(200, "text/html", new byte[OpenGraphFetch.MaxBodySize + 1]));
        Assert.Null(await _fetch.FetchDocumentAsync(new Uri("http://www.example.com")));
    }

    [Fact]
    public async Task Fetches_content_types_with_head_requests()
    {
        _web.Stub("HEAD", "http://example.com/image.png", new StubResponse(200, "image/png"));
        Assert.Equal("image/png", await _fetch.FetchContentTypeAsync(new Uri("http://example.com/image.png")));
    }

    // Metadata

    [Fact]
    public async Task Unfurls_a_page()
    {
        StubPage("http://www.example.com/", url: "https://example.com", image: "http://example.com/image.png");

        var metadata = await _unfurler.FromUrlAsync("http://www.example.com");

        Assert.True(metadata.IsValid);
        Assert.Equal("https://example.com", metadata.Url);
        Assert.Equal("Hey!", metadata.Title);
        Assert.Equal("Hello", metadata.Description);
        Assert.Equal("http://example.com/image.png", metadata.Image);
    }

    [Fact]
    public async Task Requires_title_and_description()
    {
        _web.Stub("GET", "http://www.example.com/", StubResponse.Html("<html><head></head></html>"));
        var metadata = await _unfurler.FromUrlAsync("http://www.example.com");

        Assert.False(metadata.IsValid);
        Assert.Equal(["Title can't be blank", "Description can't be blank"], metadata.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/foo")]
    public async Task Falls_back_to_the_requested_url_when_the_canonical_one_is_missing_or_invalid(string? canonical)
    {
        StubPage("http://www.example.com/foo", url: canonical, image: "http://example.com/image.png");
        var metadata = await _unfurler.FromUrlAsync("http://www.example.com/foo");

        Assert.True(metadata.IsValid);
        Assert.Equal("http://www.example.com/foo", metadata.Url);
    }

    [Fact]
    public async Task Failed_and_non_html_responses_are_invalid()
    {
        _web.Stub("GET", "http://www.example.com/", new StubResponse(403, "text/html", []));
        _web.Stub("GET", "http://www.example.com/image", new StubResponse(200, "image/jpeg", "[blob]"u8.ToArray()));

        Assert.False((await _unfurler.FromUrlAsync("http://www.example.com")).IsValid);
        Assert.False((await _unfurler.FromUrlAsync("http://www.example.com/image")).IsValid);
    }

    [Theory]
    [InlineData("/image.png")]
    [InlineData("foo")]
    [InlineData("https/incorrect")]
    [InlineData("~/etc/password")]
    public async Task Ignores_relative_and_invalid_image_urls(string image)
    {
        StubPage("http://www.example.com/", url: "https://example.com", image: image);
        var metadata = await _unfurler.FromUrlAsync("http://www.example.com");

        Assert.True(metadata.IsValid);
        Assert.Null(metadata.Image);
    }

    [Fact]
    public async Task Does_not_allow_svg_preview_images()
    {
        StubPage("http://www.example.com/", url: "https://example.com", image: "http://example.com/image.svg", imageType: "image/svg+xml");
        Assert.Null((await _unfurler.FromUrlAsync("http://www.example.com")).Image);
    }

    [Fact]
    public async Task Sanitizes_title_and_description()
    {
        StubPage("http://www.example.com/", title: "Hey!<script>alert('hi')</script>", description: "Hello<script>alert('hi')</script>");
        var metadata = await _unfurler.FromUrlAsync("http://www.example.com");

        Assert.True(metadata.IsValid);
        Assert.Equal("Hey!alert('hi')", metadata.Title);
        Assert.Equal("Helloalert('hi')", metadata.Description);
    }

    [Fact]
    public async Task Removes_entity_encoded_tags_from_title_and_description()
    {
        const string encoded = "&#x3c;&#x2f;&#x73;&#x63;&#x72;&#x69;&#x70;&#x74;&#x3e;&#x3c;&#x69;&#x6d;&#x67;&#x20;&#x73;&#x72;&#x63;&#x3d;&#x61;&#x20;&#x6f;&#x6e;&#x65;&#x72;&#x72;&#x6f;&#x72;&#x3d;&#x70;&#x72;&#x6f;&#x6d;&#x70;&#x74;&#x28;&#x31;&#x29;&#x3e;";
        StubPage("http://www.example.com/", title: "Hey!" + encoded, description: "Hello" + encoded + "</script>", rawAttributes: true);
        var metadata = await _unfurler.FromUrlAsync("http://www.example.com");

        Assert.True(metadata.IsValid);
        Assert.Equal("Hey!", metadata.Title);
        Assert.Equal("Hello", metadata.Description);
    }

    [Fact]
    public async Task Rejects_titles_and_descriptions_that_are_only_markup()
    {
        StubPage("http://www.example.com/", title: "<img src='x' onerror='alert(document.domain)'/>", description: "<img src='x' onerror='alert(document.domain)'/>");
        var metadata = await _unfurler.FromUrlAsync("http://www.example.com");

        Assert.False(metadata.IsValid);
        Assert.Equal("", metadata.Title);
        Assert.Equal("", metadata.Description);
        Assert.Contains("Title can't be blank", metadata.Errors);
        Assert.Contains("Description can't be blank", metadata.Errors);
    }

    [Theory]
    [InlineData("http://twitter.com/dhh/status/834146806594433025")]
    [InlineData("http://x.com/dhh/status/834146806594433025")]
    public async Task Unfurls_tweets_through_fxtwitter(string url)
    {
        StubPage("http://fxtwitter.com/dhh/status/834146806594433025", url: "https://example.com", title: "Hey! 🔥");
        var metadata = await _unfurler.FromUrlAsync(url);

        Assert.True(metadata.IsValid);
        Assert.Equal("Hey! 🔥", metadata.Title);
    }

    [Fact]
    public void Sanitize_keeps_text_and_escapes_markup_characters()
    {
        Assert.Equal("a &amp; b &lt; c", OpenGraphUnfurler.Sanitize("a &amp; b &lt; c"));
        Assert.Null(OpenGraphUnfurler.Sanitize(null));
    }

    private void StubPage(string pageUrl, string? url = null, string title = "Hey!", string description = "Hello", string? image = null, string imageType = "image/png", bool rawAttributes = false)
    {
        string Attribute(string value) => rawAttributes ? value : System.Net.WebUtility.HtmlEncode(value);

        var html = "<html><head>" +
                   (url is null ? "" : $"<meta property=\"og:url\" content=\"{url}\">") +
                   $"<meta property=\"og:title\" content=\"{Attribute(title)}\">" +
                   $"<meta property=\"og:description\" content=\"{Attribute(description)}\">" +
                   (image is null ? "" : $"<meta property=\"og:image\" content=\"{image}\">") +
                   "</head></html>";

        _web.Stub("GET", pageUrl, StubResponse.Html(html));
        if (image is not null && Uri.TryCreate(image, UriKind.Absolute, out var imageUri) && imageUri.Scheme == "http")
        {
            _web.Stub("HEAD", imageUri.AbsoluteUri, new StubResponse(200, imageType));
        }
    }
}
