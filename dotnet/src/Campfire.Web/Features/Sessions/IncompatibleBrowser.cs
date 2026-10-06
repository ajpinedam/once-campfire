using Campfire.Web.Views;
using Campfire.Web.Views.Sessions;
using RazorSlices;

namespace Campfire.Web.Features.Sessions;

/// <summary>Rails' <c>allow_browser ... block: -> { render template: "sessions/incompatible_browser" }</c>.</summary>
public static class IncompatibleBrowser
{
    /// <summary>Writes the "Upgrade to a supported web browser" page (status 200, as Rails' custom block renders it).</summary>
    public static Task Render(HttpContext context)
    {
        var page = IncompatibleBrowserPage.Create(new IncompatibleBrowserModel(PageContext.For(context)));
        return ((IResult)page).ExecuteAsync(context);
    }
}
