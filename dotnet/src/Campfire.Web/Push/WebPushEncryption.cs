using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Campfire.Web.Push;

/// <summary>Base64url as browsers and the Ruby web-push gem write it: padding optional, '+/' tolerated.</summary>
public static class WebPushBase64
{
    public static byte[] Decode(string value)
    {
        var normalized = value.Trim().TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Base64Url.DecodeFromChars(normalized);
    }

    public static string Encode(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);
}

/// <summary>
/// Message encryption for Web Push (RFC 8291, <c>aes128gcm</c> content coding from RFC 8188),
/// built on .NET's own ECDH P-256, HKDF and AES-GCM. The whole payload is one record.
/// </summary>
public static class WebPushEncryption
{
    public const int RecordSize = 4096;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int PublicKeySize = 65;

    /// <summary>The largest plaintext that fits the single record (minus tag and padding delimiter).</summary>
    public const int MaxPlaintextSize = RecordSize - TagSize - 1;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] ContentEncryptionKeyInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>Encrypts <paramref name="plaintext"/> for a subscription's <c>p256dh</c> key and <c>auth</c> secret.</summary>
    /// <exception cref="CryptographicException">The subscription's keys are malformed.</exception>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userAgentPublicKey, ReadOnlySpan<byte> authSecret)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return Encrypt(plaintext, userAgentPublicKey, authSecret, serverKey, salt);
    }

    /// <summary>Deterministic core, exposed for the RFC 8291 Appendix A test vector.</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userAgentPublicKey, ReadOnlySpan<byte> authSecret, ECDiffieHellman serverKey, ReadOnlySpan<byte> salt)
    {
        if (userAgentPublicKey.Length != PublicKeySize || userAgentPublicKey[0] != 0x04)
        {
            throw new CryptographicException("The subscription's p256dh key is not an uncompressed P-256 point");
        }
        if (authSecret.Length == 0)
        {
            throw new CryptographicException("The subscription's auth secret is empty");
        }
        if (plaintext.Length > MaxPlaintextSize)
        {
            throw new ArgumentException("Payload doesn't fit a single 4096-byte record", nameof(plaintext));
        }

        using var userAgentKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33].ToArray(), Y = userAgentPublicKey[33..].ToArray() }
        });

        var serverPublicKey = UncompressedPoint(serverKey.ExportParameters(includePrivateParameters: false).Q);
        var sharedSecret = serverKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);

        // IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info" || 0x00 || ua_public || as_public, 32)
        var keyInfo = new byte[KeyInfoPrefix.Length + PublicKeySize * 2];
        KeyInfoPrefix.CopyTo(keyInfo, 0);
        userAgentPublicKey.CopyTo(keyInfo.AsSpan(KeyInfoPrefix.Length));
        serverPublicKey.CopyTo(keyInfo.AsSpan(KeyInfoPrefix.Length + PublicKeySize));

        Span<byte> ikm = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, ikm, authSecret, keyInfo);

        Span<byte> contentKey = stackalloc byte[16];
        Span<byte> nonce = stackalloc byte[12];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, contentKey, salt, ContentEncryptionKeyInfo);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, nonce, salt, NonceInfo);

        // Header: salt (16) || record size (uint32 BE) || key id length (1) || key id (server public key)
        var headerLength = SaltSize + 4 + 1 + PublicKeySize;
        var padded = plaintext.Length + 1;
        var output = new byte[headerLength + padded + TagSize];
        salt.CopyTo(output);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(SaltSize), RecordSize);
        output[SaltSize + 4] = PublicKeySize;
        serverPublicKey.CopyTo(output.AsSpan(SaltSize + 5));

        // The last (only) record ends with the 0x02 padding delimiter.
        var record = new byte[padded];
        plaintext.CopyTo(record);
        record[^1] = 0x02;

        using var aes = new AesGcm(contentKey, TagSize);
        aes.Encrypt(nonce, record, output.AsSpan(headerLength, padded), output.AsSpan(headerLength + padded, TagSize));
        return output;
    }

    internal static byte[] UncompressedPoint(ECPoint point)
    {
        var bytes = new byte[PublicKeySize];
        bytes[0] = 0x04;
        point.X!.CopyTo(bytes, 1);
        point.Y!.CopyTo(bytes, 33);
        return bytes;
    }
}
