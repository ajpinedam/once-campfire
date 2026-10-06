using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Web.Security;

namespace Campfire.Web.RichText;

/// <summary>
/// Signed Global IDs for mention attachables (<c>user.attachable_sgid</c>), shaped like Rails'
/// MessageVerifier output: <c>strict_base64({"_rails":{"data":"gid://campfire/User/1","pur":"attachable"}})--hex(hmac)</c>.
/// </summary>
internal sealed partial class SignedGlobalIds(KeyRing keys)
{
    private const string Purpose = "attachable";
    private const string App = "campfire";
    private const string Separator = "--";

    private readonly byte[] _key = keys.Derive("campfire/signed-global-ids");

    public string ForUser(long userId)
    {
        var json = "{\"_rails\":{\"data\":\"gid://" + App + "/User/" + userId.ToString(CultureInfo.InvariantCulture) + "\",\"pur\":\"" + Purpose + "\"}}";
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return data + Separator + Digest(data);
    }

    /// <summary>
    /// A user id from an sgid whose signature verifies with this app's key and whose purpose is
    /// <c>attachable</c> (Action Text's <c>Attachable.from_node</c>, which <c>Content#attachables</c> uses).
    /// </summary>
    public long? VerifiedUserId(string? sgid)
    {
        if (string.IsNullOrEmpty(sgid))
        {
            return null;
        }

        var separator = sgid.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var data = sgid[..separator];
        var digest = Encoding.ASCII.GetBytes(sgid[(separator + Separator.Length)..]);
        if (!CryptographicOperations.FixedTimeEquals(digest, Encoding.ASCII.GetBytes(Digest(data))))
        {
            return null;
        }

        return Payload(data) is { Data: { } gid, Purpose: Purpose } ? UserIdFromGid(gid) : null;
    }

    /// <summary>
    /// The app's <c>attachable_from_possibly_expired_sgid</c>: mentions are signed, and rotating
    /// SECRET_KEY_BASE would otherwise orphan every existing one. The payload is read without
    /// checking the signature — acceptable only because the record must be a User, which anyone
    /// may mention anyway. Handles both the Rails 7.1+ <c>_rails.data</c> payload and Rails 7's
    /// Marshal-serialized <c>_rails.message</c> (scanned for the GID rather than unmarshaled).
    /// </summary>
    public static long? UnverifiedUserId(string? sgid)
    {
        if (string.IsNullOrEmpty(sgid))
        {
            return null;
        }

        var separator = sgid.IndexOf(Separator, StringComparison.Ordinal);
        var data = separator >= 0 ? sgid[..separator] : sgid;
        if (Payload(data) is not { } payload)
        {
            return null;
        }

        if (payload.Data is { } gid)
        {
            return UserIdFromGid(gid);
        }

        if (payload.Message is { } message && DecodeBase64(message) is { } marshaled &&
            MarshaledGid().Match(Encoding.Latin1.GetString(marshaled)) is { Success: true } match)
        {
            return UserIdFromGid(match.Value);
        }

        return null;
    }

    private string Digest(string data) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(data)));

    private sealed record RailsPayload(string? Data, string? Message, string? Purpose);

    private static RailsPayload? Payload(string data)
    {
        if (DecodeBase64(data) is not { } bytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("_rails", out var rails) || rails.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new RailsPayload(String(rails, "data"), String(rails, "message"), String(rails, "pur"));
        }
        catch (JsonException)
        {
            return null;
        }

        static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Ruby's <c>strict_decode64</c>, falling back to <c>urlsafe_decode64</c> (which tolerates missing padding).</summary>
    private static byte[]? DecodeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
        }

        try
        {
            var standard = value.Replace('-', '+').Replace('_', '/');
            if (!standard.EndsWith('=') && standard.Length % 4 != 0)
            {
                standard = standard.PadRight((standard.Length + 3) & ~3, '=');
            }
            return Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The id of <c>gid://{app}/User/{id}</c>; other models (rooms, ...) are never attachables.</summary>
    private static long? UserIdFromGid(string gid)
    {
        var match = GlobalIdPattern().Match(gid);
        return match.Success && match.Groups["model"].Value == "User" &&
               long.TryParse(Uri.UnescapeDataString(match.Groups["id"].Value), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    [GeneratedRegex(@"\Agid://(?<app>[^/]+)/(?<model>[^/]+)/(?<id>[^/?#]+)")]
    private static partial Regex GlobalIdPattern();

    [GeneratedRegex(@"gid://campfire/[^/]+/\d+")]
    private static partial Regex MarshaledGid();
}
