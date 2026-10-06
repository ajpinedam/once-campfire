using System.Globalization;
using System.Text;
using Campfire.Web.Domain;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Features.Messages;

/// <summary>
/// Rails' Messages::AttachmentPresentation: images become lightboxed thumbnails sized from the
/// analyzed dimensions, videos get an inline player with a poster frame, anything else a
/// file link with download and share buttons.
/// </summary>
public static class AttachmentPresentation
{
    public static HtmlString Render(Blob blob, BlobStore store)
    {
        if (store.IsPreviewable(blob) || BlobStore.IsVariable(blob))
        {
            return new HtmlString(blob.IsVideo ? VideoPreview(blob) : LightboxedImagePreview(blob));
        }

        return new HtmlString(FileLink(blob));
    }

    private static string VideoPreview(Blob blob)
    {
        var (width, height) = PreviewDimensions(blob);
        var video = new StringBuilder("<video");
        ViewHelpers.Attr(video, "src", BlobUrls.Blob(blob));
        ViewHelpers.Attr(video, "poster", BlobUrls.Representation(blob, VariantKind.VideoPreview));
        video.Append(" controls=\"controls\" preload=\"none\" width=\"100%\" height=\"100%\" class=\"message__attachment\"></video>");
        return DimensionConstraints(width, height, video.ToString());
    }

    private static string LightboxedImagePreview(Blob blob)
    {
        var (width, height) = PreviewDimensions(blob);
        var download = BlobUrls.Blob(blob, download: true);

        var html = new StringBuilder("<a class=\"flex\" data-lightbox-target=\"image\" data-action=\"lightbox#open\"");
        ViewHelpers.Attr(html, "data-lightbox-url-value", download);
        ViewHelpers.Attr(html, "href", BlobUrls.Blob(blob));
        html.Append("><img");
        if (width is { } w) ViewHelpers.Attr(html, "width", Number(w));
        if (height is { } h) ViewHelpers.Attr(html, "height", Number(h));
        html.Append(" class=\"message__attachment\" loading=\"lazy\"");
        ViewHelpers.Attr(html, "src", BlobUrls.Representation(blob, VariantKind.MessageThumb));
        html.Append(" /></a>");

        return DimensionConstraints(width, height, html.ToString());
    }

    // Reserves the media's space before it loads, so the transcript doesn't jump as it arrives.
    private static string DimensionConstraints(double? width, double? height, string content)
    {
        if (width is { } w && height is { } h && h > 0)
        {
            var halfWidth = w == Math.Floor(w) ? Number(Math.Floor(w / 2)) : Number(w / 2); // Ruby: Integer#/ floors
            var aspectRatio = (w / h).ToString("R", CultureInfo.InvariantCulture);
            return $"<div class=\"max-inline-size center flex overflow-clip\" style=\"width: {halfWidth}px; aspect-ratio: {aspectRatio};\">{content}</div>";
        }

        return $"<div class=\"max-inline-size center overflow-clip\">{content}</div>";
    }

    /// <summary>The analyzed size, scaled down to fit the thumbnail limits (1200x800).</summary>
    public static (double? Width, double? Height) PreviewDimensions(Blob blob)
    {
        if (blob.Metadata.Width is not { } width || blob.Metadata.Height is not { } height || width <= 0 || height <= 0)
        {
            return (null, null);
        }

        if (width <= Message.ThumbnailMaxWidth && height <= Message.ThumbnailMaxHeight)
        {
            return (width, height);
        }

        var scale = Math.Min((double)Message.ThumbnailMaxWidth / width, (double)Message.ThumbnailMaxHeight / height);
        return (width * scale, height * scale);
    }

    private static string FileLink(Blob blob)
    {
        var download = BlobUrls.Blob(blob, download: true);
        var filename = ViewHelpers.Escape(blob.Filename);

        return new StringBuilder("<div class=\"flex-inline align-center gap-half\">")
            .Append(ViewHelpers.ImageTag("common-file-text.svg", new Img(Size: 22, Class: "colorize--black", AriaHidden: true)).Value)
            .Append("<span>").Append(filename).Append("</span>")
            .Append("<a class=\"btn message__action-btn hide-in-ios-pwa\" style=\"--width: auto;\" href=\"").Append(ViewHelpers.Escape(download)).Append("\">")
            .Append(ViewHelpers.ImageTag("download.svg", new Img(Size: 20, AriaHidden: true)).Value)
            .Append("<span class=\"for-screen-reader\">Download ").Append(filename).Append("</span></a>")
            .Append("<button class=\"btn message__action-btn\" style=\"--width: auto;\" data-controller=\"web-share\" data-action=\"web-share#share\" data-web-share-files-value=\"")
            .Append(ViewHelpers.Escape(download)).Append("\">")
            .Append(ViewHelpers.ImageTag("share.svg", new Img(Size: 20, AriaHidden: true)).Value)
            .Append("<span class=\"for-screen-reader\">Share ").Append(filename).Append("</span></button>")
            .Append("</div>")
            .ToString();
    }

    private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
}

/// <summary>Rails' <c>message_sound_presentation</c>: a play button plus the sound's image or text.</summary>
public static class SoundPresentation
{
    public static HtmlString Render(Sound sound)
    {
        var html = new StringBuilder("<div class=\"sound\" data-controller=\"sound\" data-action=\"messages:play-&gt;sound#play\" data-sound-url-value=\"")
            .Append(ViewHelpers.Escape(ViewHelpers.AssetPath(sound.AssetPath)))
            .Append("\"><button class=\"btn btn--plain\" data-action=\"sound#play\">🔊</button>");

        html.Append(sound.Image is { } image
            ? ViewHelpers.ImageTag(image.AssetPath, new Img(Width: image.Width, Height: image.Height, Class: "align--middle")).Value
            : ViewHelpers.Escape(sound.Text));

        return new HtmlString(html.Append("</div>").ToString());
    }
}
