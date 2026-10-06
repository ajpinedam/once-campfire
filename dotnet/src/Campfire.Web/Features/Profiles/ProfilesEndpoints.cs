using Campfire.Web.Data;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Features.Sessions;
using Campfire.Web.Http;
using Campfire.Web.Security;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Campfire.Web.Views.Profiles;
using Campfire.Web.Views.Users;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.Profiles;

/// <summary>Users::ProfilesController: the signed-in user's own settings (the route's user id is ignored).</summary>
public static class ProfilesEndpoints
{
    public static IEndpointRouteBuilder MapProfiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/users/{user_id}/profile", Show);
        app.MapMethods("/users/{user_id}/profile", ["PATCH", "PUT"], Update);
        return app;
    }

    private static RazorSlices.RazorSlice<Views.Profiles.ProfileModel> Show(HttpContext context, Sql sql, Current current, KeyRing keys)
    {
        var user = current.User;
        var page = PageContext.For(context);
        var shared = new List<ProfileMembership>();
        var direct = new List<ProfileMembership>();

        foreach (var (membership, room) in Q.Memberships.ForUserWithRooms(sql, user.Id, visibleOnly: false))
        {
            var entry = new ProfileMembership(page, room, Q.Rooms.DisplayName(sql, room, user), membership.Involvement);
            (room.IsDirect ? direct : shared).Add(entry);
        }

        var transfer = new TransferModel(page.Url(Paths.SessionTransfer(TransferIds.For(keys, user.Id))), ForSomeoneElse: false);
        var hasAvatar = Q.Attachments.Exists(sql, "User", user.Id, "avatar");
        return Views.Profiles.Show.Create(new ProfileModel(page, user, hasAvatar, shared, direct, transfer));
    }

    private static async Task<IResult> Update(HttpContext context, Sql sql, Current current, BlobStore store)
    {
        var user = current.User;
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var avatar = Uploads.File(form, "user[avatar]");

        string? Field(string name) => form.TryGetValue($"user[{name}]", out var value) ? value.ToString() : null;

        var emailAddress = Field("email_address");
        if (!string.IsNullOrWhiteSpace(emailAddress) && Q.Users.EmailTaken(sql, emailAddress, exceptUserId: user.Id))
        {
            emailAddress = null; // taken by someone else: keep the current address
        }

        Q.Users.UpdateProfile(sql, user.Id, Field("name"), emailAddress, Field("password"), Field("bio"));

        if (avatar is not null)
        {
            await Uploads.AttachAsync(sql, store, avatar, "User", user.Id, "avatar", context.RequestAborted);
            Q.Users.Touch(sql, user.Id);
        }

        var notice = avatar is not null ? "It may take up to 30 minutes to change everywhere." : "✓";
        return Respond.Redirect(context, Paths.UserProfile, notice: notice);
    }
}
