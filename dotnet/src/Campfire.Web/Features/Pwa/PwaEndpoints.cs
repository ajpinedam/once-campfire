using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Campfire.Web.Http;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Pwa;

/// <summary>PwaController: the web app manifest and the service worker, both at stable root URLs.</summary>
public static class PwaEndpoints
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapPwa(this IEndpointRouteBuilder app)
    {
        foreach (var path in (string[])["/webmanifest", "/webmanifest.json"])
        {
            app.MapGet(path, Manifest).AllowUnauthenticated().SkipCsrf();
        }

        // A stable URL at the root, so it can't use the digested asset path.
        foreach (var path in (string[])["/service-worker", "/service-worker.js"])
        {
            app.MapGet(path, () => Results.Text(ServiceWorker, "text/javascript", Utf8)).AllowUnauthenticated().SkipCsrf();
        }

        return app;
    }

    /// <summary>pwa/manifest.json.erb, with the account's name and logo.</summary>
    private static IResult Manifest(HttpContext context, Current current)
    {
        var account = current.Account;
        string ImageUrl(string asset) => context.Request.BaseUrl() + ViewHelpers.AssetPath(asset);

        var manifest = new Dictionary<string, object>
        {
            ["name"] = account?.Name ?? "Campfire",
            ["icons"] = new object[]
            {
                new Dictionary<string, string> { ["src"] = Paths.FreshAccountLogo(account, "small"), ["type"] = "image/png", ["sizes"] = "192x192" },
                new Dictionary<string, string> { ["src"] = Paths.FreshAccountLogo(account), ["type"] = "image/png", ["sizes"] = "512x512" },
                new Dictionary<string, string> { ["src"] = Paths.FreshAccountLogo(account), ["type"] = "image/png", ["sizes"] = "512x512", ["purpose"] = "maskable" }
            },
            ["start_url"] = "/",
            ["display"] = "standalone",
            ["scope"] = "/",
            ["description"] = "A chat app from the makers of Basecamp and HEY.",
            ["categories"] = new[] { "social", "business", "productivity" },
            ["theme_color"] = "#ffffff",
            ["background_color"] = "#ffffff",
            ["shortcuts"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "New chat room",
                    ["description"] = "Open Campfire and start a new chat room",
                    ["url"] = "rooms/opens/new",
                    ["icons"] = new object[] { new Dictionary<string, string> { ["src"] = ImageUrl("add.svg"), ["sizes"] = "any" } }
                },
                new Dictionary<string, object>
                {
                    ["name"] = "My profile",
                    ["description"] = "Open Campfire and view your profile",
                    ["url"] = "/users/me/profile",
                    ["icons"] = new object[] { new Dictionary<string, string> { ["src"] = ImageUrl("person.svg"), ["sizes"] = "any" } }
                }
            },
            ["screenshots"] = new object[]
            {
                Screenshot(ImageUrl("screenshots/android-chat.png"), "Campfire is an installable, self-hosted group chat system."),
                Screenshot(ImageUrl("screenshots/android-sidebar.png"), "Easily invite people. Make rooms. @mentions, DMs, and mobile support."),
                Screenshot(ImageUrl("screenshots/android-dark-mode.png"), "Full support for dark mode, customizable to your brand.")
            }
        };

        return Results.Text(JsonSerializer.Serialize(manifest, ManifestJson), "application/json", Utf8);
    }

    private static Dictionary<string, string> Screenshot(string src, string label) => new()
    {
        ["src"] = src,
        ["sizes"] = "1080x2400",
        ["form_factor"] = "narrow",
        ["label"] = label
    };

    /// <summary>app/views/pwa/service_worker.js, verbatim.</summary>
    private const string ServiceWorker = """
self.addEventListener("push", async (event) => {
  const data = await event.data.json()
  event.waitUntil(Promise.all([ showNotification(data), updateBadgeCount(data.options) ]))
})

async function showNotification({ title, options }) {
  return self.registration.showNotification(title, options)
}

async function updateBadgeCount({ data: { badge } }) {
  return self.navigator.setAppBadge?.(badge || 0)
}

self.addEventListener("notificationclick", (event) => {
  event.notification.close()

  const url = new URL(event.notification.data.path, self.location.origin).href
  event.waitUntil(openURL(url))
})

async function openURL(url) {
  const clients = await self.clients.matchAll({ type: "window" })
  const focused = clients.find((client) => client.focused)

  if (focused) {
    await focused.navigate(url)
  } else {
    await self.clients.openWindow(url)
  }
}
""";
}
