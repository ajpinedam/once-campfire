using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Campfire.Web.Domain;

namespace Campfire.Web.Storage;

/// <summary>
/// ffprobe/ffmpeg for video attachments (Active Storage's VideoAnalyzer and VideoPreviewer).
/// Both tools are optional: without them videos are stored unanalyzed and get no poster.
/// </summary>
public static class VideoProcessing
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Rails' VideoPreviewer filter: the first frame, or the first keyframe after a scene change,
    // so posters aren't a black fade-in.
    private const string PreviewFilter = @"select=eq(n\,0)+eq(key\,1)+gt(scene\,0.015),loop=loop=-1:size=2,trim=start_frame=1";

    private static readonly Lazy<string?> Ffmpeg = new(() => FindExecutable("ffmpeg"));
    private static readonly Lazy<string?> Ffprobe = new(() => FindExecutable("ffprobe"));

    public static bool CanAnalyze => Ffprobe.Value is not null;
    public static bool CanPreview => Ffmpeg.Value is not null;

    /// <summary>Width/height (as displayed, honoring rotation) and duration in seconds.</summary>
    public static async Task<BlobMetadata> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        if (Ffprobe.Value is not { } ffprobe)
        {
            return BlobMetadata.Empty with { Analyzed = true };
        }

        var output = await RunAsync(ffprobe, ["-print_format", "json", "-show_streams", "-show_format", "-v", "error", path], cancellationToken);
        if (output is null)
        {
            return BlobMetadata.Empty with { Analyzed = true };
        }

        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            JsonElement? video = null;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                    {
                        video = stream;
                        break;
                    }
                }
            }

            int? width = null, height = null;
            double? duration = null;
            if (video is { } v)
            {
                width = v.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : null;
                height = v.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) ? hi : null;
                duration = Seconds(v, "duration");
                if (Math.Abs(Rotation(v)) % 180 == 90)
                {
                    (width, height) = (height, width);
                }
            }

            if (duration is null && root.TryGetProperty("format", out var format))
            {
                duration = Seconds(format, "duration");
            }

            return new BlobMetadata(width, height, duration, Analyzed: true);
        }
        catch (JsonException)
        {
            return BlobMetadata.Empty with { Analyzed = true };
        }
    }

    /// <summary>Extracts a representative frame as PNG to <paramref name="destination"/>. False when ffmpeg is missing or fails.</summary>
    public static async Task<bool> ExtractFrameAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (Ffmpeg.Value is not { } ffmpeg)
        {
            return false;
        }

        var output = await RunAsync(ffmpeg,
            ["-nostdin", "-v", "error", "-i", source, "-y", "-vf", PreviewFilter, "-frames:v", "1", "-f", "image2", "-c:v", "png", destination],
            cancellationToken);
        return output is not null && File.Exists(destination) && new FileInfo(destination).Length > 0;
    }

    private static double? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate) &&
            int.TryParse(rotate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees))
        {
            return degrees;
        }

        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out var rotation) && rotation.TryGetInt32(out var value))
                {
                    return value;
                }
            }
        }

        return 0;
    }

    private static async Task<string?> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            return process.ExitCode == 0 ? await stdout : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return null;
        }
    }

    private static string? FindExecutable(string name)
    {
        var candidates = OperatingSystem.IsWindows() ? [name + ".exe", name] : new[] { name };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }
}
