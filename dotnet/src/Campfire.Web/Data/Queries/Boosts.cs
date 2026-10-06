using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

public static partial class Boosts
{
    /// <summary>Creates a boost and touches its message (Rails' <c>belongs_to :message, touch: true</c>).</summary>
    public static Boost Create(Sql sql, long messageId, long boosterId, string content) => sql.Transaction(tx =>
    {
        var now = SqlTime.UtcNow();
        var id = tx.Insert("INSERT INTO boosts (message_id, booster_id, content, created_at, updated_at) VALUES (@message, @booster, @content, @now, @now)",
            ("@message", messageId), ("@booster", boosterId), ("@content", content), ("@now", now));
        Messages.Touch(tx, messageId);
        return new Boost(id, messageId, boosterId, content, now, now);
    });

    public static Boost? FindForBooster(Sql sql, long messageId, long boostId, long boosterId) =>
        sql.First($"SELECT {Rows.BoostColumns()} FROM boosts WHERE id = @id AND message_id = @message AND booster_id = @booster",
            r => Rows.ReadBoost(r), ("@id", boostId), ("@message", messageId), ("@booster", boosterId));

    public static void Delete(Sql sql, Boost boost) => sql.Transaction(tx =>
    {
        tx.Execute("DELETE FROM boosts WHERE id = @id", ("@id", boost.Id));
        Messages.Touch(tx, boost.MessageId);
    });

    /// <summary>Boosts with their boosters for a set of messages, oldest first, grouped by message id.</summary>
    public static Dictionary<long, List<(Boost Boost, User Booster)>> ForMessages(Sql sql, IEnumerable<long> messageIds)
    {
        var grouped = new Dictionary<long, List<(Boost, User)>>();
        sql.Each($"""
            SELECT {Rows.BoostColumns()}, {Rows.UserColumns()} FROM boosts JOIN users ON users.id = boosts.booster_id
            WHERE boosts.message_id IN (SELECT value FROM json_each(@ids)) ORDER BY boosts.created_at, boosts.id
            """, r =>
        {
            var boost = Rows.ReadBoost(r);
            if (!grouped.TryGetValue(boost.MessageId, out var list))
            {
                grouped[boost.MessageId] = list = [];
            }
            list.Add((boost, Rows.ReadUser(r, Rows.BoostWidth)));
        }, ("@ids", IdList.Json(messageIds)));
        return grouped;
    }
}
