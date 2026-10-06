using Campfire.Web.Data;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Features.Messages;
using Campfire.Web.Jobs;
using Campfire.Web.Storage;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.People;

/// <summary>Rails' <c>RemoveBannedContentJob</c>: deletes everything a banned user wrote and pulls it from open pages.</summary>
public sealed record RemoveBannedContentJob(long UserId) : IBackgroundJob
{
    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<BlobStore>();
        var broadcasts = services.GetRequiredService<MessageBroadcasts>();
        using var sql = services.GetRequiredService<Database>().Open();

        foreach (var message in Q.Messages.ByCreator(sql, UserId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uploads.Purge(store, Q.Messages.Delete(sql, message.Id));
            broadcasts.Removed(message);
        }

        return Task.CompletedTask;
    }
}
