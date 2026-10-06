using Campfire.Web.Domain;
using Campfire.Web.Views.Users;

namespace Campfire.Web.Views.Profiles;

/// <summary>users/profiles/show.</summary>
public sealed record ProfileModel(
    PageContext Page,
    User User,
    bool HasAvatar,
    IReadOnlyList<ProfileMembership> SharedMemberships,
    IReadOnlyList<ProfileMembership> DirectMemberships,
    TransferModel Transfer);

/// <summary>users/profiles/_membership: a room and the viewer's notification level for it.</summary>
public sealed record ProfileMembership(PageContext Page, Room Room, string DisplayName, Involvement Involvement);
