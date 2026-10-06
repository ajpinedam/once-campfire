using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Data;

/// <summary>
/// Time values are stored exactly as the Rails SQLite adapter stores them
/// (UTC text, <c>yyyy-MM-dd HH:mm:ss.ffffff</c>) so databases stay interchangeable and
/// lexicographic comparison in SQL matches chronological order.
/// </summary>
public static class SqlTime
{
    private const string StorageFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    public static string Format(DateTime time) =>
        time.ToUniversalTime().ToString(StorageFormat, CultureInfo.InvariantCulture);

    public static string Now() => Format(DateTime.UtcNow);

    /// <summary>Microseconds are the storage precision; truncating keeps round-trips equal.</summary>
    public static DateTime UtcNow()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
    }

    public static DateTime Parse(ReadOnlySpan<char> text)
    {
        // Fast path for the canonical layout: "2024-01-02 03:04:05[.ffffff]"
        if (text.Length >= 19 && text[4] == '-' && text[7] == '-' && (text[10] == ' ' || text[10] == 'T') && text[13] == ':' && text[16] == ':')
        {
            var ticks = 0L;
            if (text.Length > 20 && text[19] == '.')
            {
                var fraction = text[20..];
                var digits = 0;
                while (digits < fraction.Length && char.IsAsciiDigit(fraction[digits]) && digits < 7)
                {
                    ticks = ticks * 10 + (fraction[digits] - '0');
                    digits++;
                }
                for (var i = digits; i < 7; i++)
                {
                    ticks *= 10;
                }
            }

            return new DateTime(
                Digits(text, 0, 4), Digits(text, 5, 2), Digits(text, 8, 2),
                Digits(text, 11, 2), Digits(text, 14, 2), Digits(text, 17, 2), DateTimeKind.Utc).AddTicks(ticks);
        }

        return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public static DateTime Get(SqliteDataReader reader, int ordinal) => Parse(reader.GetString(ordinal));

    public static DateTime? GetNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));

    /// <summary>Milliseconds since the Unix epoch, matching Rails' <c>to_fs(:epoch)</c> and JS <c>getTime()</c>.</summary>
    public static long ToEpochMilliseconds(DateTime time) =>
        (time.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    public static DateTime FromEpochMilliseconds(long milliseconds) =>
        DateTime.UnixEpoch.AddTicks(milliseconds * TimeSpan.TicksPerMillisecond);

    /// <summary>Rails' <c>to_fs(:number)</c>: <c>yyyyMMddHHmmss</c>, used as a cache-busting version.</summary>
    public static string ToNumber(DateTime time) =>
        time.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>ISO 8601 as Rails' <c>Time#iso8601</c> emits it for UTC times.</summary>
    public static string ToIso8601(DateTime time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static int Digits(ReadOnlySpan<char> text, int start, int length)
    {
        var value = 0;
        for (var i = start; i < start + length; i++)
        {
            value = value * 10 + (text[i] - '0');
        }
        return value;
    }
}
