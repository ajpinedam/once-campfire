namespace Campfire.Tests.Support;

// Inside the namespace, so sibling test namespaces (Campfire.Tests.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Security;

/// <summary>
/// The Rails test fixtures (test/fixtures/*.yml), created through the app's own queries.
/// Every human's password is <see cref="CampfireApp.Password"/>.
/// </summary>
public sealed record Fixtures(
    Account Account,
    User David,
    User Jason,
    User Jz,
    User Kevin,
    User Bender,
    Room Pets,
    Room Hq,
    Room Watercooler,
    Room Designers,
    Room DavidAndJason,
    Room DavidAndKevin,
    Room BenderAndKevin)
{
    public static Fixtures Seed(CampfireApp app)
    {
        // Touch Services so the host builds and migrates the database first.
        _ = app.Services;
        return app.Sql(sql => sql.Transaction(tx =>
        {
            var account = Accounts.Create(tx, "37signals");

            var david = Users.Create(tx, "David", "david@37signals.com", CampfireApp.Password, UserRole.Administrator);
            var jason = Users.Create(tx, "Jason", "jason@37signals.com", CampfireApp.Password, UserRole.Administrator);
            var jz = Users.Create(tx, "JZ", "jz@37signals.com", CampfireApp.Password, bio: "Designer");
            var kevin = Users.Create(tx, "Kevin", "kevin@37signals.com", CampfireApp.Password, bio: "Programmer");
            var bender = Users.Create(tx, "Bender Bot", null, null, UserRole.Bot, botToken: SecureTokens.BotToken());

            // Rooms are created without the open-room auto-grant so memberships match the fixtures exactly.
            var pets = Rooms.Create(tx, "All Pets", RoomType.Open, david.Id);
            var hq = Rooms.Create(tx, "HQ", RoomType.Open, david.Id);
            var watercooler = Rooms.Create(tx, "All Talk", RoomType.Closed, david.Id);
            var designers = Rooms.Create(tx, "Designers", RoomType.Closed, david.Id);
            var davidAndJason = Rooms.Create(tx, null, RoomType.Direct, david.Id);
            var davidAndKevin = Rooms.Create(tx, null, RoomType.Direct, david.Id);
            var benderAndKevin = Rooms.Create(tx, null, RoomType.Direct, kevin.Id);

            void Member(Room room, User user, Involvement involvement = Involvement.Mentions) =>
                tx.Execute("""
                    INSERT INTO memberships (room_id, user_id, involvement, connections, created_at, updated_at)
                    VALUES (@room, @user, @involvement, 0, @now, @now)
                    """, ("@room", room.Id), ("@user", user.Id), ("@involvement", involvement.ToStored()), ("@now", SqlTime.UtcNow()));

            Member(designers, david);
            Member(designers, jason, Involvement.Everything);
            Member(designers, jz, Involvement.Everything);
            Member(designers, kevin);
            Member(pets, david, Involvement.Everything);
            Member(pets, jason, Involvement.Everything);
            Member(watercooler, david, Involvement.Everything);
            Member(watercooler, jason, Involvement.Everything);
            Member(watercooler, bender);
            Member(hq, david, Involvement.Everything);
            Member(hq, jason, Involvement.Everything);
            Member(hq, jz, Involvement.Everything);
            Member(hq, kevin, Involvement.Everything);
            Member(davidAndJason, david, Involvement.Everything);
            Member(davidAndJason, jason, Involvement.Everything);
            Member(davidAndKevin, david, Involvement.Everything);
            Member(davidAndKevin, kevin, Involvement.Everything);
            Member(benderAndKevin, bender, Involvement.Everything);
            Member(benderAndKevin, kevin, Involvement.Everything);

            return new Fixtures(account, david, jason, jz, kevin, bender, pets, hq, watercooler, designers, davidAndJason, davidAndKevin, benderAndKevin);
        }));
    }
}
