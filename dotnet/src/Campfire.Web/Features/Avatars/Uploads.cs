using Campfire.Web.Data;
using Campfire.Web.Storage;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.Avatars;

/// <summary>
/// <c>has_one_attached</c> assignment from a form upload: store the file, attach it in place of
/// any previous one, and purge the replaced file.
/// </summary>
public static class Uploads
{
    /// <summary>The uploaded file for a form field, or null when the field was left empty.</summary>
    public static IFormFile? File(IFormCollection form, string field) =>
        form.Files.GetFile(field) is { Length: > 0 } file ? file : null;

    public static async Task AttachAsync(Sql sql, BlobStore store, IFormFile file, string recordType, long recordId, string name, CancellationToken cancellationToken)
    {
        await using var content = file.OpenReadStream();
        var blob = await store.CreateAsync(sql, content, Path.GetFileName(file.FileName), file.ContentType, cancellationToken);
        Purge(store, Q.Attachments.Attach(sql, recordType, recordId, name, blob.Id));
    }

    public static void Detach(Sql sql, BlobStore store, string recordType, long recordId, string name) =>
        Purge(store, Q.Attachments.Detach(sql, recordType, recordId, name));

    public static void Purge(BlobStore store, IReadOnlyCollection<string> keys)
    {
        if (keys.Count > 0)
        {
            store.Purge(keys);
        }
    }
}
