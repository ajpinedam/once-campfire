using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Campfire.Web.Security;

/// <summary>
/// Purpose-specific HMAC keys derived from SECRET_KEY_BASE with HKDF, so a token minted for one
/// purpose (a cookie, an avatar URL, a stream name...) can never be replayed as another.
/// </summary>
public sealed class KeyRing
{
    private readonly byte[] _secretKeyBase;

    public KeyRing(byte[] secretKeyBase)
    {
        _secretKeyBase = secretKeyBase;
        Cookies = new MessageSigner(Derive("campfire/cookies"));
        Flash = new MessageSigner(Derive("campfire/flash"));
        StreamNames = new MessageSigner(Derive("campfire/turbo-stream-names"));
        SignedIds = new SignedIds(Derive("campfire/signed-ids"));
        GlobalIds = new MessageSigner(Derive("campfire/signed-global-ids"));
        Csrf = Derive("campfire/csrf");
    }

    public MessageSigner Cookies { get; }
    public MessageSigner Flash { get; }
    public MessageSigner StreamNames { get; }
    public MessageSigner GlobalIds { get; }
    public SignedIds SignedIds { get; }
    public byte[] Csrf { get; }

    public byte[] Derive(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _secretKeyBase, 32, salt: [], info: Encoding.UTF8.GetBytes(purpose));
}

/// <summary>
/// Signs strings as <c>{base64url(value)}--{base64url(hmac)}</c>, the same shape as Rails'
/// MessageVerifier output. Verification is constant time.
/// </summary>
public sealed class MessageSigner(byte[] key)
{
    private const string Separator = "--";
    private const int DigestLength = 43;

    public string Sign(string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        return $"{Base64Url.EncodeToString(data)}{Separator}{Base64Url.EncodeToString(HMACSHA256.HashData(key, data))}";
    }

    public bool TryVerify(string? signed, out string value)
    {
        value = "";
        if (string.IsNullOrEmpty(signed))
        {
            return false;
        }

        // The digest is always 43 characters (32 bytes, unpadded base64url). Split at that fixed
        // position: base64url's alphabet includes '-', so the payload or digest can itself contain "--".
        var separator = signed.Length - DigestLength - Separator.Length;
        if (separator <= 0 || !signed.AsSpan(separator, Separator.Length).SequenceEqual(Separator))
        {
            return false;
        }

        try
        {
            var data = Base64Url.DecodeFromChars(signed.AsSpan(0, separator));
            var digest = Base64Url.DecodeFromChars(signed.AsSpan(separator + Separator.Length));
            if (!CryptographicOperations.FixedTimeEquals(digest, HMACSHA256.HashData(key, data)))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string? Verify(string? signed) => TryVerify(signed, out var value) ? value : null;
}

/// <summary>
/// Rails' <c>signed_id(purpose:, expires_in:)</c>: an opaque, tamper-proof reference to a record
/// id for one purpose (avatar URLs, session transfer links, blob URLs).
/// Format: <c>{id}[.{expiresUnix}]--{base64url(truncated hmac)}</c>.
/// </summary>
public sealed class SignedIds(byte[] key)
{
    public string Generate(long id, string purpose, TimeSpan? expiresIn = null)
    {
        var payload = expiresIn is { } ttl
            ? $"{id}.{DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds()}"
            : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{payload}--{Digest(payload, purpose)}";
    }

    public long? Find(string? token, string purpose)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var separator = token.IndexOf("--", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var payload = token[..separator];
        var expected = Digest(payload, purpose);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token[(separator + 2)..]), Encoding.ASCII.GetBytes(expected)))
        {
            return null;
        }

        var dot = payload.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (!long.TryParse(payload.AsSpan(dot + 1), out var expires) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
            {
                return null;
            }
            payload = payload[..dot];
        }

        return long.TryParse(payload, out var id) ? id : null;
    }

    private string Digest(string payload, string purpose)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{purpose}\n{payload}"), mac);
        return Base64Url.EncodeToString(mac[..16]);
    }
}
