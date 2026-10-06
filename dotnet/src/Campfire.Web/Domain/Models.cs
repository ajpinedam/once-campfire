using System.Text.RegularExpressions;

namespace Campfire.Web.Domain;

public sealed record AccountSettings(bool RestrictRoomCreationToAdministrators)
{
    public static readonly AccountSettings Default = new(RestrictRoomCreationToAdministrators: false);
}

public sealed record Account(
    long Id,
    string Name,
    string JoinCode,
    string? CustomStyles,
    AccountSettings Settings,
    bool HasLogo,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public const string DefaultName = "Campfire";
}

public sealed partial record User(
    long Id,
    string Name,
    string? EmailAddress,
    UserRole Role,
    UserStatus Status,
    string? Bio,
    string? BotToken,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public const string MentionContentType = "application/vnd.campfire.mention";

    public bool IsAdministrator => Role == UserRole.Administrator;
    public bool IsBot => Role == UserRole.Bot;
    public bool IsMember => Role == UserRole.Member;
    public bool IsActive => Status == UserStatus.Active;
    public bool IsDeactivated => Status == UserStatus.Deactivated;
    public bool IsBanned => Status == UserStatus.Banned;

    /// <summary><c>"#{id}-#{bot_token}"</c>, the credential bots put in their URLs.</summary>
    public string BotKey => $"{Id}-{BotToken}";

    /// <summary>Rails: <c>name.scan(/\b\w/).join</c>.</summary>
    public string Initials => string.Concat(WordStart().Matches(Name).Select(m => m.Value));

    /// <summary>Rails: <c>[ name, bio ].compact_blank.join(" – ")</c>.</summary>
    public string Title => string.IsNullOrWhiteSpace(Bio) ? Name : $"{Name} – {Bio}";

    /// <summary>What a mention of this user reads as in plain text.</summary>
    public string MentionText => $"@{Name}";

    /// <summary>Rails' <c>can_administer?(record = nil)</c> for users with no record in mind.</summary>
    public bool CanAdminister() => IsAdministrator;

    /// <summary>Administrators can administer anything; creators can administer what they created.</summary>
    public bool CanAdminister(long creatorId) => IsAdministrator || creatorId == Id;

    [GeneratedRegex(@"\b\w")]
    private static partial Regex WordStart();
}

public sealed record Room(
    long Id,
    string? Name,
    RoomType Type,
    long CreatorId,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public bool IsOpen => Type == RoomType.Open;
    public bool IsClosed => Type == RoomType.Closed;
    public bool IsDirect => Type == RoomType.Direct;

    public Involvement DefaultInvolvement => IsDirect ? Involvement.Everything : Involvement.Mentions;
}

public sealed record Membership(
    long Id,
    long RoomId,
    long UserId,
    Involvement Involvement,
    DateTime? UnreadAt,
    DateTime? ConnectedAt,
    int Connections,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static readonly TimeSpan ConnectionTtl = TimeSpan.FromSeconds(60);

    public bool IsUnread => UnreadAt is not null;
    public bool IsVisible => Involvement != Involvement.Invisible;

    public bool IsConnected(DateTime now) => ConnectedAt is { } connectedAt && connectedAt >= now - ConnectionTtl;
}

public sealed record Message(
    long Id,
    long RoomId,
    long CreatorId,
    string ClientMessageId,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public const int PageSize = 40;
    public const int ThumbnailMaxWidth = 1200;
    public const int ThumbnailMaxHeight = 800;
}

public sealed record Boost(
    long Id,
    long MessageId,
    long BoosterId,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public const int MaxContentLength = 16;
}

public sealed record Session(
    long Id,
    long UserId,
    string Token,
    string? IpAddress,
    string? UserAgent,
    DateTime LastActiveAt,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static readonly TimeSpan ActivityRefreshRate = TimeSpan.FromHours(1);
}

public sealed record Search(long Id, long UserId, string Query, DateTime CreatedAt, DateTime UpdatedAt)
{
    public const int RecentLimit = 10;
}

public sealed record Ban(long Id, long UserId, string IpAddress, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record Webhook(long Id, long UserId, string? Url, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record PushSubscription(
    long Id,
    long UserId,
    string? Endpoint,
    string? P256dhKey,
    string? AuthKey,
    string? UserAgent,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Metadata an analyzed blob carries (Rails stores it as JSON text).</summary>
public sealed record BlobMetadata(int? Width, int? Height, double? Duration, bool Analyzed)
{
    public static readonly BlobMetadata Empty = new(null, null, null, false);
}

/// <summary>An Active Storage blob row: a stored file and what we know about it.</summary>
public sealed record Blob(
    long Id,
    string Key,
    string Filename,
    string? ContentType,
    BlobMetadata Metadata,
    long ByteSize,
    string? Checksum,
    DateTime CreatedAt)
{
    public bool IsImage => ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsVideo => ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
}
