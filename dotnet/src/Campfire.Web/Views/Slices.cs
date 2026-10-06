using RazorSlices;

namespace Campfire.Web.Views;

/// <summary>
/// Renders slices outside the HTTP pipeline — for Turbo Stream broadcasts, stream responses and
/// cached fragments — always with <see cref="MinimalHtmlEncoder"/> (the pipeline gets it from DI).
/// </summary>
public static class Slices
{
    public static async Task<string> RenderAsync(RazorSlice slice, CancellationToken cancellationToken = default)
    {
        using (slice)
        {
            return await slice.RenderAsync(MinimalHtmlEncoder.Instance, cancellationToken);
        }
    }

    public static async Task<byte[]> RenderUtf8Async(RazorSlice slice, CancellationToken cancellationToken = default)
    {
        using (slice)
        {
            using var buffer = new MemoryStream(4096);
            await slice.RenderAsync(buffer, MinimalHtmlEncoder.Instance, cancellationToken);
            return buffer.ToArray();
        }
    }
}
