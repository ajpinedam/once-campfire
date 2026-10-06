namespace Campfire.Web.Domain;

/// <summary>Stored as integers, matching Rails' <c>enum :role, %i[ member administrator bot ]</c>.</summary>
public enum UserRole
{
    Member = 0,
    Administrator = 1,
    Bot = 2
}

/// <summary>Stored as integers, matching Rails' <c>enum :status, %i[ active deactivated banned ]</c>.</summary>
public enum UserStatus
{
    Active = 0,
    Deactivated = 1,
    Banned = 2
}

/// <summary>Single-table inheritance discriminator, stored as the Rails class name in <c>rooms.type</c>.</summary>
public enum RoomType
{
    Open,
    Closed,
    Direct
}

/// <summary>Stored as lowercase strings in <c>memberships.involvement</c>.</summary>
public enum Involvement
{
    Invisible,
    Nothing,
    Mentions,
    Everything
}

public static class StoredValues
{
    public const string OpenRoomType = "Rooms::Open";
    public const string ClosedRoomType = "Rooms::Closed";
    public const string DirectRoomType = "Rooms::Direct";

    public static string ToStored(this RoomType type) => type switch
    {
        RoomType.Open => OpenRoomType,
        RoomType.Closed => ClosedRoomType,
        RoomType.Direct => DirectRoomType,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static RoomType ParseRoomType(string stored) => stored switch
    {
        OpenRoomType => RoomType.Open,
        ClosedRoomType => RoomType.Closed,
        DirectRoomType => RoomType.Direct,
        _ => throw new ArgumentOutOfRangeException(nameof(stored), stored, "Unknown room type")
    };

    public static string ToStored(this Involvement involvement) => involvement switch
    {
        Involvement.Invisible => "invisible",
        Involvement.Nothing => "nothing",
        Involvement.Mentions => "mentions",
        Involvement.Everything => "everything",
        _ => throw new ArgumentOutOfRangeException(nameof(involvement))
    };

    public static Involvement ParseInvolvement(string? stored) => stored switch
    {
        "invisible" => Involvement.Invisible,
        "nothing" => Involvement.Nothing,
        "everything" => Involvement.Everything,
        _ => Involvement.Mentions
    };

    public static bool TryParseInvolvement(string? value, out Involvement involvement)
    {
        involvement = ParseInvolvement(value);
        return value is "invisible" or "nothing" or "mentions" or "everything";
    }

    public static string ToStored(this UserRole role) => role switch
    {
        UserRole.Member => "member",
        UserRole.Administrator => "administrator",
        UserRole.Bot => "bot",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
