using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Net;
using Campfire.Web.Push;
using Campfire.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Campfire.Tests.Integrations.Support;

/// <summary>DNS for tests: hosts answer with whatever addresses the test says (Resolv.stubs).</summary>
public sealed class FakeHostResolver : IHostResolver
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<IPAddress>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private int _lookups;

    /// <summary>Answer for hosts without their own entry; empty = unresolvable.</summary>
    public IReadOnlyList<IPAddress> Default { get; set; } = [];

    public int Lookups => _lookups;

    public FakeHostResolver Answer(string host, params string[] addresses)
    {
        _answers[host] = addresses.Select(IPAddress.Parse).ToArray();
        return this;
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookups);
        return Task.FromResult(_answers.TryGetValue(host, out var answer) ? answer : Default);
    }
}

/// <summary>A canned response for <see cref="StubWeb"/>.</summary>
public sealed record StubResponse(int Status, string? ContentType = null, byte[]? Body = null, IReadOnlyDictionary<string, string>? Headers = null, TimeSpan? Delay = null, long? ContentLength = null)
{
    public static StubResponse Html(string html) => new(200, "text/html", System.Text.Encoding.UTF8.GetBytes(html));
    public static StubResponse Redirect(string location) => new(302, Headers: new Dictionary<string, string> { ["Location"] = location });
}

/// <summary>
/// A real HTTP server on loopback answering like WebMock stubs, keyed by method and absolute URL
/// (<c>http://{Host header}{path}</c>), so code under test can believe it's talking to any host.
/// </summary>
public sealed class StubWeb : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, StubResponse> _stubs = new(StringComparer.OrdinalIgnoreCase);

    private StubWeb(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }
    public ConcurrentQueue<(string Method, string Url, byte[] Body, string? ContentType)> Requests { get; } = new();

    public static async Task<StubWeb> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        StubWeb? web = null;
        app.Run(async context => await web!.HandleAsync(context));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        web = new StubWeb(app, new Uri(address).Port);
        return web;
    }

    public StubWeb Stub(string method, string url, StubResponse response)
    {
        _stubs[$"{method} {url}"] = response;
        return this;
    }

    /// <summary>A dialer for <see cref="PinnedHttpClient"/>: records the pinned endpoint, connects here instead.</summary>
    public Func<IPEndPoint, CancellationToken, ValueTask<Stream>> Dialer(ConcurrentQueue<IPEndPoint> dialed) => async (endpoint, cancellationToken) =>
    {
        dialed.Enqueue(endpoint);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    };

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var url = $"http://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
        Requests.Enqueue((context.Request.Method, url, buffer.ToArray(), context.Request.ContentType));

        if (!_stubs.TryGetValue($"{context.Request.Method} {url}", out var stub) &&
            !_stubs.TryGetValue($"{context.Request.Method} http://{context.Request.Host.Host}{context.Request.Path}", out stub))
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (stub.Delay is { } delay)
        {
            await Task.Delay(delay, context.RequestAborted);
        }

        context.Response.StatusCode = stub.Status;
        if (stub.ContentType is not null) context.Response.ContentType = stub.ContentType;
        foreach (var (name, value) in stub.Headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[name] = value;
        }

        if (stub.ContentLength is { } length)
        {
            // A lying Content-Length: advertise one size, send fewer bytes, then drop the connection.
            // Then hold the connection until the client hangs up. Aborting right away sends a TCP RST,
            // and on Linux an RST that lands before the client reads makes it discard the headers too.
            context.Response.ContentLength = length;
            await context.Response.StartAsync();
            if (stub.Body is not null) await context.Response.Body.WriteAsync(stub.Body);
            await context.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
            context.Abort();
            return;
        }

        if (stub.Body is not null && !HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(stub.Body);
        }
    }
}

/// <summary>Records pushes instead of sending them; answers with <see cref="Status"/>.</summary>
public sealed class RecordingPushTransport : IWebPushTransport
{
    public ConcurrentQueue<(WebPushRequest Request, IPAddress Address)> Sent { get; } = new();
    public int Status { get; set; } = 201;

    public Task<int> SendAsync(WebPushRequest request, IPAddress address, CancellationToken cancellationToken)
    {
        Sent.Enqueue((request, address));
        return Task.FromResult(Status);
    }
}

/// <summary>A browser's push subscription keys (P-256 key pair + auth secret), to subscribe and to decrypt with.</summary>
public sealed class BrowserPushKeys : IDisposable
{
    public BrowserPushKeys()
    {
        Key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = Key.ExportParameters(false).Q;
        PublicKey = [0x04, .. q.X!, .. q.Y!];
        AuthSecret = RandomNumberGenerator.GetBytes(16);
    }

    public ECDiffieHellman Key { get; }
    public byte[] PublicKey { get; }
    public byte[] AuthSecret { get; }
    public string P256dh => WebPushBase64.Encode(PublicKey);
    public string Auth => WebPushBase64.Encode(AuthSecret);

    public byte[] Decrypt(byte[] message) => WebPushDecryption.Decrypt(message, Key, PublicKey, AuthSecret);

    public void Dispose() => Key.Dispose();
}

/// <summary>The user agent's side of RFC 8291, written independently of the app's encryptor.</summary>
public static class WebPushDecryption
{
    public static byte[] Decrypt(byte[] message, ECDiffieHellman userAgentKey, byte[] userAgentPublicKey, byte[] authSecret)
    {
        var salt = message[..16];
        var recordSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16, 4));
        Assert.Equal(4096u, recordSize);
        int idLength = message[20];
        var serverPublicKey = message[21..(21 + idLength)];
        var ciphertext = message[(21 + idLength)..];

        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublicKey[1..33], Y = serverPublicKey[33..] }
        });
        var secret = userAgentKey.DeriveRawSecretAgreement(server.PublicKey);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. userAgentPublicKey, .. serverPublicKey];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var plaintext = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plaintext);

        var end = Array.LastIndexOf(plaintext, (byte)0x02);
        Assert.True(end >= 0 && plaintext[(end + 1)..].All(b => b == 0), "last record must end with the 0x02 delimiter");
        return plaintext[..end];
    }
}

/// <summary>A real VAPID key pair for tests, base64url-encoded like the web-push gem writes them.</summary>
public sealed record VapidTestKeys(string PrivateKey, string PublicKey)
{
    public static VapidTestKeys Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        return new VapidTestKeys(WebPushBase64.Encode(parameters.D), WebPushBase64.Encode([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]));
    }
}

public static class IntegrationsApp
{
    /// <summary>
    /// The app with its outside world replaced: fake DNS, recorded pushes, real VAPID keys, and
    /// any further service overrides. Shares <paramref name="app"/>'s database.
    /// </summary>
    public static WebApplicationFactory<Program> With(CampfireApp app, FakeHostResolver resolver, IWebPushTransport? transport = null, Action<IServiceCollection>? services = null)
    {
        var vapid = VapidTestKeys.Generate();
        return app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("VAPID_PUBLIC_KEY", vapid.PublicKey);
            builder.UseSetting("VAPID_PRIVATE_KEY", vapid.PrivateKey);
            builder.ConfigureTestServices(test =>
            {
                test.AddSingleton<IHostResolver>(resolver);
                test.AddSingleton<IWebPushTransport>(transport ?? new RecordingPushTransport());
                services?.Invoke(test);
            });
        });
    }

    /// <summary>An HttpClient signed in as <paramref name="user"/>, sending CSRF tokens, not following redirects.</summary>
    public static HttpClient SignedIn(WebApplicationFactory<Program> factory, User user, string userAgent = "Mozilla/5.0")
    {
        var services = factory.Services;
        using var sql = services.GetRequiredService<Campfire.Web.Data.Database>().Open();
        var session = Campfire.Web.Data.Queries.Sessions.Start(sql, user.Id, userAgent, "127.0.0.1");
        var keys = services.GetRequiredService<KeyRing>();
        var seed = Guid.NewGuid().ToString("N");
        var token = services.GetRequiredService<Csrf>().MaskedToken(seed);

        var client = factory.CreateDefaultClient(new SessionHandler($"{AppCookies.SessionToken}={keys.Cookies.Sign(session.Token)}; {Csrf.CookieName}={seed}", token));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }
            await Task.Delay(10);
        }
    }

    private sealed class SessionHandler(string cookie, string csrfToken) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("Cookie", cookie);
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                request.Headers.Add(Csrf.HeaderName, csrfToken);
            }
            if (request.Headers.Accept.Count == 0)
            {
                request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
