namespace Campfire.Web.Data.Queries;

/// <summary>action_text_rich_texts: the HTML bodies attached to records (only messages use them).</summary>
public static partial class RichTexts
{
    public static string? Find(Sql sql, string recordType, long recordId, string name = "body") =>
        sql.ScalarString("SELECT body FROM action_text_rich_texts WHERE record_type = @type AND record_id = @id AND name = @name",
            ("@type", recordType), ("@id", recordId), ("@name", name));

    public static Dictionary<long, string> ForMessages(Sql sql, IEnumerable<long> messageIds)
    {
        var bodies = new Dictionary<long, string>();
        sql.Each("""
            SELECT record_id, body FROM action_text_rich_texts
            WHERE record_type = 'Message' AND name = 'body' AND record_id IN (SELECT value FROM json_each(@ids)) AND body IS NOT NULL
            """, r => bodies[r.GetInt64(0)] = r.GetString(1), ("@ids", IdList.Json(messageIds)));
        return bodies;
    }

    public static void Upsert(Sql sql, string recordType, long recordId, string name, string body)
    {
        var now = SqlTime.UtcNow();
        sql.Execute("""
            INSERT INTO action_text_rich_texts (record_type, record_id, name, body, created_at, updated_at)
            VALUES (@type, @id, @name, @body, @now, @now)
            ON CONFLICT (record_type, record_id, name) DO UPDATE SET body = excluded.body, updated_at = excluded.updated_at
            """, ("@type", recordType), ("@id", recordId), ("@name", name), ("@body", body), ("@now", now));
    }
}
