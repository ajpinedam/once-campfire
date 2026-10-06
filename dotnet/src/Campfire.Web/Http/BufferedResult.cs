using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using RazorSlices;

namespace Campfire.Web.Http;

/// <summary>
/// Renders a page into a pooled buffer, then sends it in one write with a Content-Length.
/// Templates issue thousands of small writes; straight to Kestrel each one takes the response
/// pipe's lock (contended by the socket flush), which profiled at ~10% of a page's CPU.
/// </summary>
public sealed class BufferedResult(IResult inner) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var original = httpContext.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new PooledBufferWriter();
        httpContext.Features.Set<IHttpResponseBodyFeature>(new BufferedBodyFeature(buffer));
        try
        {
            await inner.ExecuteAsync(httpContext);
        }
        finally
        {
            httpContext.Features.Set(original);
        }

        httpContext.Response.ContentLength = buffer.WrittenCount;
        if (buffer.WrittenCount > 0)
        {
            await buffer.CopyToAsync(original.Writer, httpContext.RequestAborted);
        }
    }

    /// <summary>Endpoint filter: buffers every Razor slice a handler returns.</summary>
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        return result is RazorSlice slice ? new BufferedResult(slice) : result;
    }

    private sealed class BufferedBodyFeature(PooledBufferWriter writer) : IHttpResponseBodyFeature
    {
        private Stream? _stream;

        public Stream Stream => _stream ??= writer.AsStream();
        public PipeWriter Writer => writer;
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync() => Task.CompletedTask;

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            SendFileFallback.SendFileAsync(Stream, path, offset, count, cancellationToken);
    }
}

/// <summary>
/// A PipeWriter over a list of pooled segments: growing never copies what's written (a large page
/// is ~10 segments), and the segments are handed to the response in order.
/// </summary>
internal sealed class PooledBufferWriter : PipeWriter, IDisposable
{
    private const int SegmentSize = 64 * 1024;

    private readonly List<(byte[] Array, int Length)> _full = [];
    private byte[] _current = ArrayPool<byte>.Shared.Rent(SegmentSize);
    private int _position;

    public int WrittenCount { get; private set; }

    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => WrittenCount;

    public override void Advance(int bytes)
    {
        _position += bytes;
        WrittenCount += bytes;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsMemory(_position);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsSpan(_position);
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));
    public override void CancelPendingFlush() { }
    public override void Complete(Exception? exception = null) { }

    /// <summary>Writes every segment to <paramref name="destination"/>, then flushes once.</summary>
    public async ValueTask CopyToAsync(PipeWriter destination, CancellationToken cancellationToken)
    {
        foreach (var (array, length) in _full)
        {
            destination.Write(array.AsSpan(0, length));
        }
        destination.Write(_current.AsSpan(0, _position));
        await destination.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        foreach (var (array, _) in _full)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
        _full.Clear();
        ArrayPool<byte>.Shared.Return(_current);
        _current = [];
    }

    private void Ensure(int sizeHint)
    {
        var needed = Math.Max(sizeHint, 256);
        if (_current.Length - _position >= needed)
        {
            return;
        }

        _full.Add((_current, _position));
        _current = ArrayPool<byte>.Shared.Rent(Math.Max(SegmentSize, needed));
        _position = 0;
    }
}
