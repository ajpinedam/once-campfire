using System.Security.Cryptography;
using System.Text;
using Campfire.Web.Configuration;

namespace Campfire.Web.Push;

/// <summary>
/// Voluntary Application Server Identification (RFC 8292): an ES256 JWT, signed with the VAPID
/// private key, naming the push service's origin, so push services know who's sending.
/// </summary>
public sealed class VapidSigner : IDisposable
{
    public const string Subject = "mailto:support@37signals.com";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly ECDsa _key;

    private VapidSigner(ECDsa key, string publicKey)
    {
        _key = key;
        PublicKey = publicKey;
    }

    /// <summary>The application server key, base64url (what the browser subscribed with).</summary>
    public string PublicKey { get; }

    /// <summary>Builds a signer from base64url keys: the raw 32-byte private scalar and the 65-byte uncompressed public point.</summary>
    /// <exception cref="CryptographicException">The keys are malformed or don't belong together.</exception>
    public static VapidSigner Create(string privateKey, string publicKey)
    {
        byte[] d, q;
        try
        {
            d = WebPushBase64.Decode(privateKey);
            q = WebPushBase64.Decode(publicKey);
        }
        catch (FormatException error)
        {
            throw new CryptographicException("VAPID keys aren't valid base64url", error);
        }

        if (d.Length is 0 or > 32 || q.Length != 65 || q[0] != 0x04)
        {
            throw new CryptographicException("VAPID keys must be a 32-byte P-256 private key and a 65-byte uncompressed public key");
        }

        // A scalar with leading zero bytes may come encoded shorter than 32 bytes.
        var scalar = new byte[32];
        d.CopyTo(scalar, 32 - d.Length);

        var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = scalar,
            Q = new ECPoint { X = q[1..33], Y = q[33..] }
        });

        return new VapidSigner(key, WebPushBase64.Encode(q));
    }

    /// <summary>Signer from settings, or null when web push isn't configured or the keys are unusable.</summary>
    public static VapidSigner? FromSettings(CampfireSettings settings, ILogger logger)
    {
        if (!settings.WebPushEnabled)
        {
            logger.LogInformation("VAPID keys aren't configured; web push notifications are disabled");
            return null;
        }

        try
        {
            return Create(settings.VapidPrivateKey!, settings.VapidPublicKey!);
        }
        catch (CryptographicException)
        {
            logger.LogError("VAPID_PRIVATE_KEY/VAPID_PUBLIC_KEY are invalid; web push notifications are disabled");
            return null;
        }
    }

    /// <summary>The <c>Authorization</c> header value for a push to <paramref name="endpoint"/>.</summary>
    public string AuthorizationHeader(Uri endpoint, DateTimeOffset now) => $"vapid t={Token(endpoint, now)}, k={PublicKey}";

    public string Token(Uri endpoint, DateTimeOffset now)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var header = WebPushBase64.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = WebPushBase64.Encode(Encoding.UTF8.GetBytes(
            $$"""{"aud":"{{audience}}","exp":{{(now + Lifetime).ToUnixTimeSeconds()}},"sub":"{{Subject}}"}"""));

        var signingInput = $"{header}.{claims}";
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{WebPushBase64.Encode(signature)}";
    }

    public void Dispose() => _key.Dispose();
}
