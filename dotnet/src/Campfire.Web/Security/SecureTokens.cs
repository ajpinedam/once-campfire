using System.Security.Cryptography;

namespace Campfire.Web.Security;

/// <summary>Random tokens in the alphabets the Rails app used, so formats stay recognizable.</summary>
public static class SecureTokens
{
    private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string Base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary><c>SecureRandom.alphanumeric(12).scan(/.{4}/).join("-")</c>, e.g. <c>aB3d-9xYz-Q2w7</c>.</summary>
    public static string JoinCode()
    {
        var code = RandomNumberGenerator.GetString(Alphanumeric, 12);
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    /// <summary><c>SecureRandom.alphanumeric(12)</c>.</summary>
    public static string BotToken() => RandomNumberGenerator.GetString(Alphanumeric, 12);

    /// <summary><c>has_secure_token</c>: 24 base58 characters.</summary>
    public static string SessionToken() => RandomNumberGenerator.GetString(Base58, 24);

    /// <summary>Active Storage blob keys: 28 lowercase base36 characters.</summary>
    public static string BlobKey() => RandomNumberGenerator.GetString(Base36, 28);
}

/// <summary>bcrypt, as Rails' <c>has_secure_password</c> uses it ($2a$, cost 12), so existing digests verify.</summary>
public static class Passwords
{
    public const int MaxBytes = 72;
    /// <summary>bcrypt cost; tests lower it (Rails uses MIN_COST in test too).</summary>
    internal static int WorkFactor { get; set; } = 12;

    // A real digest to compare against when no user matches, so failures take the same time.
    private static readonly Lazy<string> Dummy = new(() => BCrypt.Net.BCrypt.HashPassword("dummy-password", WorkFactor));

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(Truncate(password), WorkFactor);

    public static bool Verify(string password, string digest)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(Truncate(password), digest);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    public static void VerifyAgainstDummy(string password) => _ = Verify(password, Dummy.Value);

    // bcrypt only ever reads the first 72 bytes.
    private static string Truncate(string password)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        return bytes.Length <= MaxBytes ? password : System.Text.Encoding.UTF8.GetString(bytes, 0, MaxBytes);
    }
}
