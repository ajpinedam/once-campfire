using Campfire.Web.Http;
using Campfire.Web.Views;
using QRCoder;

namespace Campfire.Web.Features.QrCodeImages;

/// <summary>QrCodeController: an SVG QR code for a URL (join links, auto-login links).</summary>
public static class QrCodeEndpoints
{
    public static IEndpointRouteBuilder MapQrCodes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/qr_code/{id}", Show).AllowUnauthenticated();
        return app;
    }

    private static IResult Show(HttpContext context, string id)
    {
        if (QrCodes.UrlFromId(id) is not { Length: > 0 } url)
        {
            return Results.NotFound();
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(11, "#000000", "#ffffff", drawQuietZones: false, SvgQRCode.SizingMode.ViewBoxAttribute);

        context.Response.Headers.CacheControl = "max-age=31536000, public";
        return Results.Text(svg, "image/svg+xml; charset=utf-8");
    }
}
