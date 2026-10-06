using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

/// <summary>
/// active_storage_attachments / active_storage_blobs: which blob is attached to which record.
/// Files on disk are managed by <c>Campfire.Web.Storage.BlobStore</c>.
/// </summary>
public static partial class Attachments
{
    public const string ServiceName = "local";

    public static Blob? Find(Sql sql, string recordType, long recordId, string name) =>
        sql.First($"""
            SELECT {Rows.BlobColumns("b")} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id
            WHERE a.record_type = @type AND a.record_id = @id AND a.name = @name ORDER BY a.id DESC LIMIT 1
            """, r => Rows.ReadBlob(r), ("@type", recordType), ("@id", recordId), ("@name", name));

    public static bool Exists(Sql sql, string recordType, long recordId, string name) =>
        sql.Exists("SELECT 1 FROM active_storage_attachments WHERE record_type = @type AND record_id = @id AND name = @name",
            ("@type", recordType), ("@id", recordId), ("@name", name));

    /// <summary>Attachment blobs for many records at once, keyed by record id.</summary>
    public static Dictionary<long, Blob> ForRecords(Sql sql, string recordType, string name, IEnumerable<long> recordIds)
    {
        var blobs = new Dictionary<long, Blob>();
        sql.Each($"""
            SELECT a.record_id, {Rows.BlobColumns("b")} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id
            WHERE a.record_type = @type AND a.name = @name AND a.record_id IN (SELECT value FROM json_each(@ids))
            """, r => blobs[r.GetInt64(0)] = Rows.ReadBlob(r, 1),
            ("@type", recordType), ("@name", name), ("@ids", IdList.Json(recordIds)));
        return blobs;
    }

    public static Blob? FindBlob(Sql sql, long blobId) =>
        sql.First($"SELECT {Rows.BlobColumns()} FROM active_storage_blobs WHERE id = @id", r => Rows.ReadBlob(r), ("@id", blobId));

    public static Blob InsertBlob(Sql sql, string key, string filename, string? contentType, BlobMetadata metadata, long byteSize, string checksum)
    {
        var now = SqlTime.UtcNow();
        var id = sql.Insert("""
            INSERT INTO active_storage_blobs (key, filename, content_type, metadata, service_name, byte_size, checksum, created_at)
            VALUES (@key, @filename, @type, @metadata, @service, @size, @checksum, @now)
            """, ("@key", key), ("@filename", filename), ("@type", contentType), ("@metadata", BlobMetadataJson.Serialize(metadata)),
            ("@service", ServiceName), ("@size", byteSize), ("@checksum", checksum), ("@now", now));
        return new Blob(id, key, filename, contentType, metadata, byteSize, checksum, now);
    }

    public static void UpdateBlobMetadata(Sql sql, long blobId, BlobMetadata metadata) =>
        sql.Execute("UPDATE active_storage_blobs SET metadata = @metadata WHERE id = @id",
            ("@metadata", BlobMetadataJson.Serialize(metadata)), ("@id", blobId));

    /// <summary>
    /// Attaches a blob to a record (has_one_attached semantics: any previous attachment under the
    /// same name is detached). Returns keys of blobs that are no longer attached anywhere.
    /// </summary>
    public static List<string> Attach(Sql sql, string recordType, long recordId, string name, long blobId) => sql.Transaction(tx =>
    {
        var orphans = Detach(tx, recordType, recordId, name);
        tx.Execute("INSERT INTO active_storage_attachments (record_type, record_id, name, blob_id, created_at) VALUES (@type, @id, @name, @blob, @now)",
            ("@type", recordType), ("@id", recordId), ("@name", name), ("@blob", blobId), ("@now", SqlTime.UtcNow()));
        return orphans;
    });

    /// <summary>Removes an attachment and its blob row; returns the purged blob keys for file deletion.</summary>
    public static List<string> Detach(Sql sql, string recordType, long recordId, string name) => sql.Transaction(tx =>
    {
        var blobs = tx.Query("""
            SELECT b.id, b.key FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id
            WHERE a.record_type = @type AND a.record_id = @id AND a.name = @name
            """, r => (Id: r.GetInt64(0), Key: r.GetString(1)), ("@type", recordType), ("@id", recordId), ("@name", name));

        tx.Execute("DELETE FROM active_storage_attachments WHERE record_type = @type AND record_id = @id AND name = @name",
            ("@type", recordType), ("@id", recordId), ("@name", name));
        DeleteBlobRows(tx, blobs.Select(blob => blob.Id));
        return blobs.Select(blob => blob.Key).ToList();
    });

    /// <summary>Deletes blob rows (and their variant records) that no attachment references anymore.</summary>
    public static void DeleteBlobRows(Sql sql, IEnumerable<long> blobIds)
    {
        var ids = IdList.Json(blobIds);
        if (ids == "[]")
        {
            return;
        }

        sql.Execute("""
            DELETE FROM active_storage_variant_records WHERE blob_id IN (SELECT value FROM json_each(@ids))
              AND blob_id NOT IN (SELECT blob_id FROM active_storage_attachments)
            """, ("@ids", ids));
        sql.Execute("""
            DELETE FROM active_storage_blobs WHERE id IN (SELECT value FROM json_each(@ids))
              AND id NOT IN (SELECT blob_id FROM active_storage_attachments)
            """, ("@ids", ids));
    }
}
