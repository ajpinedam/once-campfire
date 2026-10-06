using Campfire.Web.Cable;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Jobs;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.People;

/// <summary>Rails' <c>User#deactivate</c> and <c>User::Bannable</c>.</summary>
public sealed class UserModeration(CableServer cable, BackgroundQueue jobs, ILogger<UserModeration> logger)
{
    /// <summary>Removes someone from the account: drops shared memberships, sessions and their live connections.</summary>
    public void Deactivate(Sql sql, User user)
    {
        Q.Users.Deactivate(sql, user.Id);
        CloseRemoteConnections(user.Id);
    }

    /// <summary>
    /// Bans the addresses the user signed in from, signs them out everywhere, and removes what
    /// they wrote in the background. Addresses that aren't public (loopback, private networks) are
    /// skipped rather than failing the whole ban, which is what an unbannable session IP did in Rails.
    /// </summary>
    public void Ban(Sql sql, User user)
    {
        sql.Transaction(tx =>
        {
            foreach (var ip in Q.Sessions.DistinctIpAddresses(tx, user.Id).Where(BanAddresses.IsPublic))
            {
                Q.Bans.Create(tx, user.Id, ip);
            }

            Q.Sessions.DeleteAllForUser(tx, user.Id);
            Q.Users.SetStatus(tx, user.Id, UserStatus.Banned);
        });

        CloseRemoteConnections(user.Id);
        jobs.Enqueue(new RemoveBannedContentJob(user.Id));
    }

    public static void Unban(Sql sql, User user) => sql.Transaction(tx =>
    {
        Q.Bans.DeleteForUser(tx, user.Id);
        Q.Users.SetStatus(tx, user.Id, UserStatus.Active);
    });

    // The database change has already happened; a cable hiccup mustn't turn it into an error page.
    private void CloseRemoteConnections(long userId)
    {
        try
        {
            cable.DisconnectUser(userId, reconnect: false);
        }
        catch (Exception error)
        {
            logger.LogWarning("Could not close remote connections for user {UserId}: {Error}", userId, error.GetType().Name);
        }
    }
}
