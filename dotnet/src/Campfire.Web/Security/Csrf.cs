using System.Buffers.Text;
using System.Security.Cryptography;

namespace Campfire.Web.Security;

/// <summary>
/// CSRF protection equivalent to Rails' <c>protect_from_forgery</c>. A random seed lives in an
/// HttpOnly cookie; the token is HMAC(seed), masked with a fresh one-time pad on every render
/// (as Rails does, to defeat BREACH). Turbo sends it as <c>X-CSRF-Token</c>; plain forms post it
/// as <c>authenticity_token</c>.
/// </summary>
public sealed class Csrf(KeyRing keys)
{
    public const string CookieName = "_campfire_csrf";
    public const string HeaderName = "X-CSRF-Token";
    public const string FormFieldName = "authenticity_token";
    private const int TokenLength = 32;

    /// <summary>A masked token for this request, issuing the seed cookie if the browser has none yet.</summary>
    public string MaskedToken(HttpContext context, bool secureCookies) => MaskedToken(SeedFor(context, secureCookies));

    /// <summary>A masked token for a given seed cookie value.</summary>
    public string MaskedToken(string seed)
    {
        var raw = RawToken(seed);
        Span<byte> masked = stackalloc byte[TokenLength * 2];
        RandomNumberGenerator.Fill(masked[..TokenLength]);
        for (var i = 0; i < TokenLength; i++)
        {
            masked[TokenLength + i] = (byte)(masked[i] ^ raw[i]);
        }
        return Base64Url.EncodeToString(masked);
    }

    public bool IsValid(HttpContext context, string? submitted)
    {
        if (string.IsNullOrEmpty(submitted) || context.Request.Cookies[CookieName] is not { Length: > 0 } seed)
        {
            return false;
        }

        byte[] masked;
        try
        {
            masked = Base64Url.DecodeFromChars(submitted);
        }
        catch (FormatException)
        {
            return false;
        }

        if (masked.Length != TokenLength * 2)
        {
            return false;
        }

        Span<byte> unmasked = stackalloc byte[TokenLength];
        for (var i = 0; i < TokenLength; i++)
        {
            unmasked[i] = (byte)(masked[i] ^ masked[TokenLength + i]);
        }

        return CryptographicOperations.FixedTimeEquals(unmasked, RawToken(seed));
    }

    private byte[] RawToken(string seed) => HMACSHA256.HashData(keys.Csrf, System.Text.Encoding.ASCII.GetBytes(seed));

    private static string SeedFor(HttpContext context, bool secureCookies)
    {
        if (context.Items[CookieName] is string issued)
        {
            return issued;
        }

        if (context.Request.Cookies[CookieName] is { Length: > 0 } existing)
        {
            return existing;
        }

        var seed = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        context.Items[CookieName] = seed;
        context.Response.Cookies.Append(CookieName, seed, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = secureCookies,
            Expires = DateTimeOffset.UtcNow.AddYears(20),
            Path = "/"
        });
        return seed;
    }
}
