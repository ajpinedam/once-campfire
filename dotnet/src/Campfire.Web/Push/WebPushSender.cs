using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Campfire.Web.Configuration;
using Campfire.Web.Net;

namespace Campfire.Web.Push;

/// <summary>The notification a service worker shows (see app/views/pwa/service_worker.js).</summary>
public sealed record WebPushMessage(string Title, string Body, string Path, long Badge, string Icon)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// <c>{"title":…,"options":{"body":…,"icon":…,"data":{"path":…,"badge":…}}}</c>, as
    /// lib/web_push/notification.rb generates it. Long bodies are shortened to fit one encrypted record.
    /// </summary>
    public byte[] ToJson()
    {
        var body = Body;
        var json = Serialize(body);
        while (json.Length > WebPushEncryption.MaxPlaintextSize && body.Length > 0)
        {
            // Keep the longest prefix whose UTF-8 size drops the overflow (plus the ellipsis);
            // JSON escaping can make the cut fall short, so repeat until it fits.
            var budget = Encoding.UTF8.GetByteCount(body) - (json.Length - WebPushEncryption.MaxPlaintextSize) - 3;
            body = budget <= 0 ? "" : string.Concat(Utf8Prefix(body, budget), "…");
            json = Serialize(body);
        }
        return json;
    }

    private static ReadOnlySpan<char> Utf8Prefix(string text, int maxBytes)
    {
        var bytes = 0;
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return text.AsSpan(0, length);
    }

    private byte[] Serialize(string body)
    {
        using var buffer = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("title", Title);
            writer.WriteStartObject("options");
            writer.WriteString("body", body);
            writer.WriteString("icon", Icon);
            writer.WriteStartObject("data");
            writer.WriteString("path", Path);
            writer.WriteNumber("badge", Badge);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

/// <summary>One notification bound for one subscription.</summary>
public sealed record PushDelivery(long SubscriptionId, string? Endpoint, string? P256dhKey, string? AuthKey, WebPushMessage Message);

public enum WebPushOutcome
{
    Delivered,
    /// <summary>The push service says the subscription is gone (404/410), or its keys are unusable.</summary>
    Invalid,
    /// <summary>Not attempted: push isn't configured, or the endpoint no longer passes validation.</summary>
    Skipped,
    Failed
}

/// <summary>An encrypted push ready to POST.</summary>
public sealed record WebPushRequest(Uri Endpoint, byte[] Body, string Authorization, int TimeToLive, string Urgency);

/// <summary>The network half of a push, swappable in tests.</summary>
public interface IWebPushTransport
{
    /// <summary>POSTs the push over a connection pinned to <paramref name="address"/>; returns the status code.</summary>
    Task<int> SendAsync(WebPushRequest request, IPAddress address, CancellationToken cancellationToken);
}

public sealed class HttpWebPushTransport : IWebPushTransport, IDisposable
{
    // Rails kept a persistent pool of 150 connections for the 50 delivery threads.
    private readonly PinnedHttpClient _http = new(connectTimeout: TimeSpan.FromSeconds(10), requestTimeout: TimeSpan.FromSeconds(30), maxConnectionsPerServer: 150);

    public async Task<int> SendAsync(WebPushRequest request, IPAddress address, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new ByteArrayContent(request.Body)
        };
        message.Content.Headers.ContentType = new("application/octet-stream");
        message.Content.Headers.ContentEncoding.Add("aes128gcm");
        message.Headers.TryAddWithoutValidation("TTL", request.TimeToLive.ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("Urgency", request.Urgency);
        message.Headers.TryAddWithoutValidation("Authorization", request.Authorization);

        using var response = await _http.SendAsync(message, address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (int)response.StatusCode;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Delivers one notification (Rails' WebPush::Notification#deliver): re-validates and resolves the
/// endpoint on the delivering thread (never on the enqueue path), encrypts, signs and POSTs.
/// </summary>
public sealed class WebPushSender : IDisposable
{
    private const int TimeToLive = 4 * 7 * 24 * 60 * 60; // the web-push gem's default: four weeks

    private readonly PushEndpoints _endpoints;
    private readonly IWebPushTransport _transport;
    private readonly ILogger<WebPushSender> _logger;
    private readonly VapidSigner? _vapid;

    public WebPushSender(CampfireSettings settings, PushEndpoints endpoints, IWebPushTransport transport, ILogger<WebPushSender> logger)
    {
        _endpoints = endpoints;
        _transport = transport;
        _logger = logger;
        _vapid = VapidSigner.FromSettings(settings, logger);
    }

    public bool Enabled => _vapid is not null;

    public async Task<WebPushOutcome> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken)
    {
        if (_vapid is null)
        {
            return WebPushOutcome.Skipped;
        }

        var address = await _endpoints.ResolvedEndpointIpAsync(delivery.Endpoint, cancellationToken);
        if (address is null)
        {
            return WebPushOutcome.Skipped;
        }

        var endpoint = new Uri(delivery.Endpoint!);
        byte[] body;
        try
        {
            body = WebPushEncryption.Encrypt(delivery.Message.ToJson(), WebPushBase64.Decode(delivery.P256dhKey ?? ""), WebPushBase64.Decode(delivery.AuthKey ?? ""));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            // Rails invalidated on OpenSSL errors: a subscription whose keys can't be used never will be.
            return WebPushOutcome.Invalid;
        }

        var request = new WebPushRequest(endpoint, body, _vapid.AuthorizationHeader(endpoint, DateTimeOffset.UtcNow), TimeToLive, "high");
        var status = await _transport.SendAsync(request, address, cancellationToken);

        switch (status)
        {
            case >= 200 and < 300:
                return WebPushOutcome.Delivered;
            case 404 or 410:
                return WebPushOutcome.Invalid;
            default:
                _logger.LogWarning("Push service {Host} answered {Status} for subscription {Id}", endpoint.Host, status, delivery.SubscriptionId);
                return WebPushOutcome.Failed;
        }
    }

    public void Dispose() => _vapid?.Dispose();
}
