using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Campfire.Web.Cable;

/// <summary>A subscription's presence on one stream: where to deliver, and the head its frames start with.</summary>
internal sealed record StreamSubscriber(CableConnection Connection, byte[] Head);

/// <summary>
/// In-process pub/sub (Rails used Redis). Each stream maps to an immutable array of subscribers,
/// replaced copy-on-write, so publishing is a lock-free read and a loop.
/// </summary>
internal sealed class CablePubSub
{
    private readonly ConcurrentDictionary<string, ImmutableArray<StreamSubscriber>> _streams = new(StringComparer.Ordinal);

    public void Subscribe(string stream, StreamSubscriber subscriber) =>
        _streams.AddOrUpdate(stream,
            static (_, added) => [added],
            static (_, existing, added) => existing.Add(added),
            subscriber);

    public void Unsubscribe(string stream, StreamSubscriber subscriber)
    {
        while (_streams.TryGetValue(stream, out var existing))
        {
            var remaining = existing.Remove(subscriber);
            if (remaining.Length == existing.Length)
            {
                return;
            }

            // Only swap (or drop) the exact snapshot we read; retry if someone else changed it meanwhile.
            var swapped = remaining.IsEmpty
                ? _streams.TryRemove(KeyValuePair.Create(stream, existing))
                : _streams.TryUpdate(stream, remaining, existing);

            if (swapped)
            {
                return;
            }
        }
    }

    public void Publish(string stream, ReadOnlyMemory<byte> message)
    {
        if (!_streams.TryGetValue(stream, out var subscribers))
        {
            return;
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Connection.Enqueue(new OutgoingFrame(subscriber.Head, message, CableFrames.MessageTail));
        }
    }

    public int SubscriberCount(string stream) => _streams.TryGetValue(stream, out var subscribers) ? subscribers.Length : 0;
}
