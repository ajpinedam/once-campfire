using System.Globalization;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Security;

namespace Campfire.Web.Views;

/// <summary>
/// Rails' route helpers (<c>room_path</c>, <c>fresh_user_avatar_path</c>, ...). Every URL the app
/// emits comes from here, so the .NET routes and the templates can't drift apart.
/// Configured once at startup with the key ring (avatar tokens are signed).
/// </summary>
public static class Paths
{
    private static SignedIds? _signedIds;

    public static void Configure(KeyRing keys)
    {
        _signedIds = keys.SignedIds;
        AvatarTokens.Clear(); // a new key (tests start several apps) signs differently
    }

    private static SignedIds SignedIds => _signedIds ?? throw new InvalidOperationException("Paths.Configure must run at startup");

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    public const string Root = "/";
    public const string FirstRun = "/first_run";
    public const string NewSession = "/session/new";
    public const string Session = "/session";
    public static string SessionTransfer(string transferId) => $"/session/transfers/{Uri.EscapeDataString(transferId)}";

    // Account
    public const string EditAccount = "/account/edit";
    public const string Account = "/account";
    public const string AccountUsers = "/account/users";
    public static string AccountUser(long userId) => $"/account/users/{Id(userId)}";
    public const string AccountBots = "/account/bots";
    public const string NewAccountBot = "/account/bots/new";
    public static string AccountBot(long botId) => $"/account/bots/{Id(botId)}";
    public static string EditAccountBot(long botId) => $"/account/bots/{Id(botId)}/edit";
    public static string AccountBotKey(long botId) => $"/account/bots/{Id(botId)}/key";
    public const string AccountJoinCode = "/account/join_code";
    public const string AccountLogo = "/account/logo";
    public const string EditAccountCustomStyles = "/account/custom_styles/edit";
    public const string AccountCustomStyles = "/account/custom_styles";

    /// <summary><c>fresh_account_logo_path</c>: versioned by the account's updated_at so a new logo busts caches.</summary>
    public static string FreshAccountLogo(Account? account, string? size = null)
    {
        var version = account is null ? null : SqlTime.ToNumber(account.UpdatedAt);
        return (version, size) switch
        {
            (null, null) => AccountLogo,
            (null, _) => $"{AccountLogo}?size={size}",
            (_, null) => $"{AccountLogo}?v={version}",
            _ => $"{AccountLogo}?v={version}&size={size}"
        };
    }

    public static string Join(string joinCode) => $"/join/{Uri.EscapeDataString(joinCode)}";
    public static string QrCode(string id) => $"/qr_code/{id}";

    // Users
    public static string User(long userId) => $"/users/{Id(userId)}";
    public static string UserBan(long userId) => $"/users/{Id(userId)}/ban";
    public const string UserSidebar = "/users/me/sidebar";
    public const string UserProfile = "/users/me/profile";
    public const string UserPushSubscriptions = "/users/me/push_subscriptions";
    public static string UserPushSubscription(long id) => $"/users/me/push_subscriptions/{Id(id)}";
    public static string UserPushSubscriptionTestNotifications(long id) => $"/users/me/push_subscriptions/{Id(id)}/test_notifications";

    // Avatar tokens are deterministic per user and appear many times on every page, so each is
    // signed once. Bounded by the number of people on the account.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> AvatarTokens = new();

    /// <summary>The signed token that stands in for a user id in avatar URLs (<c>user.avatar_token</c>).</summary>
    public static string AvatarToken(long userId) =>
        AvatarTokens.TryGetValue(userId, out var token) ? token : AvatarTokens[userId] = SignedIds.Generate(userId, "avatar");

    public static long? UserIdFromAvatarToken(string token) => SignedIds.Find(token, "avatar");

    public static string UserAvatar(long userId) => $"/users/{AvatarToken(userId)}/avatar";

    /// <summary><c>fresh_user_avatar_path(user)</c>: versioned by updated_at so a new avatar busts caches.</summary>
    public static string FreshUserAvatar(User user) => $"{UserAvatar(user.Id)}?v={SqlTime.ToNumber(user.UpdatedAt)}";

    public const string AutocompletableUsers = "/autocompletable/users";
    public static string AutocompletableUsersInRoom(long roomId) => $"/autocompletable/users?room_id={Id(roomId)}";

    // Rooms
    public const string Rooms = "/rooms";
    public static string Room(long roomId) => $"/rooms/{Id(roomId)}";
    public static string RoomAtMessage(long roomId, long messageId) => $"/rooms/{Id(roomId)}/@{Id(messageId)}";
    public static string RoomMessages(long roomId) => $"/rooms/{Id(roomId)}/messages";
    public static string RoomMessage(long roomId, long messageId) => $"/rooms/{Id(roomId)}/messages/{Id(messageId)}";
    public static string EditRoomMessage(long roomId, long messageId) => $"/rooms/{Id(roomId)}/messages/{Id(messageId)}/edit";
    public static string RoomRefresh(long roomId) => $"/rooms/{Id(roomId)}/refresh";
    public static string RoomInvolvement(long roomId) => $"/rooms/{Id(roomId)}/involvement";
    public static string RoomInvolvement(long roomId, Involvement involvement) => $"/rooms/{Id(roomId)}/involvement?involvement={involvement.ToStored()}";
    public static string RoomBotMessages(long roomId, string botKey) => $"/rooms/{Id(roomId)}/{botKey}/messages";
    public static string RoomBotMessage(long roomId, string botKey, long messageId) => $"/rooms/{Id(roomId)}/{botKey}/messages/{Id(messageId)}";

    public const string NewOpenRoom = "/rooms/opens/new";
    public const string OpenRooms = "/rooms/opens";
    public static string OpenRoom(long roomId) => $"/rooms/opens/{Id(roomId)}";
    public static string EditOpenRoom(long roomId) => $"/rooms/opens/{Id(roomId)}/edit";
    public const string NewClosedRoom = "/rooms/closeds/new";
    public const string ClosedRooms = "/rooms/closeds";
    public static string ClosedRoom(long roomId) => $"/rooms/closeds/{Id(roomId)}";
    public static string EditClosedRoom(long roomId) => $"/rooms/closeds/{Id(roomId)}/edit";
    public const string NewDirectRoom = "/rooms/directs/new";
    public const string DirectRooms = "/rooms/directs";
    public static string DirectRoom(long roomId) => $"/rooms/directs/{Id(roomId)}";
    public static string EditDirectRoom(long roomId) => $"/rooms/directs/{Id(roomId)}/edit";

    /// <summary><c>rooms_directs_path(user_ids: [ id ])</c>.</summary>
    public static string DirectRoomsWith(long userId) => $"/rooms/directs?user_ids%5B%5D={Id(userId)}";

    /// <summary><c>polymorphic_path([:edit, room])</c>: each room type edits through its own controller.</summary>
    public static string EditRoom(Room room) => room.Type switch
    {
        RoomType.Open => EditOpenRoom(room.Id),
        RoomType.Closed => EditClosedRoom(room.Id),
        _ => EditDirectRoom(room.Id)
    };

    // Messages & boosts
    public static string MessageBoosts(long messageId) => $"/messages/{Id(messageId)}/boosts";
    public static string NewMessageBoost(long messageId) => $"/messages/{Id(messageId)}/boosts/new";
    public static string MessageBoost(long messageId, long boostId) => $"/messages/{Id(messageId)}/boosts/{Id(boostId)}";

    // Searches
    public const string Searches = "/searches";
    public static string SearchesFor(string query) => $"/searches?q={Uri.EscapeDataString(query)}";
    public const string ClearSearches = "/searches/clear";

    public const string UnfurlLink = "/unfurl_link";
    public const string WebManifest = "/webmanifest.json";
    public const string ServiceWorker = "/service-worker";
    public const string Cable = "/cable";
}

/// <summary>
/// Rails' <c>dom_id</c>. Messages are keyed by client_message_id (Message#to_key), so the
/// optimistic copy the composer inserts and the server-rendered message share an id.
/// Rooms always use the <c>room</c> key, whatever their type.
/// </summary>
public static class DomId
{
    public static string For(Message message, string? prefix = null) => Prefixed(prefix, $"message_{message.ClientMessageId}");
    public static string For(Room room, string? prefix = null) => Prefixed(prefix, $"room_{room.Id.ToString(CultureInfo.InvariantCulture)}");
    public static string Room(long roomId, string? prefix = null) => Prefixed(prefix, $"room_{roomId.ToString(CultureInfo.InvariantCulture)}");
    public static string For(Boost boost, string? prefix = null) => Prefixed(prefix, $"boost_{boost.Id.ToString(CultureInfo.InvariantCulture)}");
    public static string For(User user, string? prefix = null) => Prefixed(prefix, $"user_{user.Id.ToString(CultureInfo.InvariantCulture)}");
    public static string For(Membership membership, string? prefix = null) => Prefixed(prefix, $"membership_{membership.Id.ToString(CultureInfo.InvariantCulture)}");

    private static string Prefixed(string? prefix, string key) => prefix is null ? key : $"{prefix}_{key}";
}
