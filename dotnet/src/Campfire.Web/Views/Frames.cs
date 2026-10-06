using Campfire.Web.Http;
using RazorSlices;

namespace Campfire.Web.Views;

/// <summary>Model for <c>Views/Layouts/FramePage.cshtml</c>.</summary>
public sealed record FramePageModel(PageContext Page, RazorSlice Frame, string? Title = null);

/// <summary>
/// turbo-rails' layout rule for templates that render a <c>&lt;turbo-frame&gt;</c>: when Turbo asks for
/// the frame (the <c>Turbo-Frame</c> header) the frame alone is enough — Turbo extracts it either way —
/// but a plain request (a reload, a crawler, a benchmark) gets the full application layout, as Rails renders it.
/// </summary>
public static class Frames
{
    public static IResult Respond(HttpContext context, PageContext page, RazorSlice frame, string? title = null) =>
        context.Request.TurboFrame() is not null
            ? frame
            : Layouts.FramePage.Create(new FramePageModel(page, frame, title));
}
