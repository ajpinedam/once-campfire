using System.Net;
using System.Text.Encodings.Web;
using Campfire.Web.Assets;
using Campfire.Web.Cable;
using Campfire.Web.Configuration;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Features;
using Campfire.Web.Http;
using Campfire.Web.Jobs;
using Campfire.Web.Json;
using Campfire.Web.Push;
using Campfire.Web.RichText;
using Campfire.Web.Security;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

if (args is ["admin", .. var command])
{
    return Campfire.Web.Admin.AdminCommands.Run(command, () => CampfireSettings.FromConfiguration(builder.Configuration, builder.Environment));
}

var settings = CampfireSettings.FromConfiguration(builder.Configuration, builder.Environment);
var keys = new KeyRing(settings.SecretKeyBase);
var database = new Database(settings.DatabasePath);

const long MaxUploadBytes = 1L << 30; // attachments: Rails/Puma imposed no limit; 1 GiB keeps a sane ceiling
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = MaxUploadBytes;
});

var services = builder.Services;
services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(form => form.MultipartBodyLengthLimit = MaxUploadBytes);

services.AddSingleton(settings);
services.AddSingleton(AssetCatalog.Load(Path.Combine(builder.Environment.WebRootPath, "assets")));
services.AddSingleton(keys);
services.AddSingleton(database);
services.AddScoped(_ => database.Open()); // one pooled connection per request, opened on first use
services.AddSingleton<HtmlEncoder>(MinimalHtmlEncoder.Instance);
services.AddSingleton<Csrf>();
services.AddSingleton<AppCookies>();
services.AddSingleton<AccountCache>();
services.AddSingleton<Authentication>();
services.AddSingleton<WriteBatcher>();
services.AddHostedService(provider => provider.GetRequiredService<WriteBatcher>());
services.AddSingleton<BackgroundQueue>();
services.AddHostedService<BackgroundWorker>();

services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    json.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    // Trust X-Forwarded-* only from proxies on private networks (Rails' trusted_proxies), so
    // a client can't spoof its address to dodge bans or rate limits.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var network in (string[])["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"])
    {
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

services.AddRateLimiter(limiter => limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests);

services
    .AddCableModule()
    .AddRichTextModule()
    .AddStorageModule()
    .AddPushModule()
    .AddRoomsFeatures()
    .AddAccountFeatures();

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(database.Dispose); // closes the pooled connections

Schema.Migrate(database);
using (var sql = database.Open())
{
    Memberships.DisconnectAll(sql); // nobody can still be connected to a process that just started
}

Paths.Configure(keys);
ViewHelpers.Configure(keys);

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errors => errors.Run(context => PublicPages.Serve(context, StatusCodes.Status500InternalServerError)));
}

if (settings.ForceSsl)
{
    // Rails' assume_ssl + force_ssl: TLS is terminated in front of the app (Thruster, kamal-proxy),
    // so every request is treated as HTTPS (secure cookies, https URLs) and gets HSTS.
    app.Use((context, next) =>
    {
        context.Request.Scheme = "https";
        return next(context);
    });
    app.UseHsts();
}

app.UseStatusCodePages(context => PublicPages.ServeIfEmpty(context.HttpContext));
app.UseMiddleware<AssetMiddleware>();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseHttpMethodOverride(new HttpMethodOverrideOptions { FormFieldName = "_method" });
app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<CampfireRequestMiddleware>();

app.MapCableModule();

// CSRF is enforced by CampfireRequestMiddleware (Rails-compatible tokens), not ASP.NET antiforgery.
var endpoints = app.MapGroup("").DisableAntiforgery().AddEndpointFilter(BufferedResult.Filter);
endpoints.MapStorageModule();
endpoints.MapPushModule();
endpoints.MapRoomsFeatures();
endpoints.MapAccountFeatures();

app.Run();
return 0;

/// <summary>Rails' public/404.html, 422.html and 500.html for HTML requests that got no body.</summary>
internal static class PublicPages
{
    public static Task ServeIfEmpty(HttpContext context)
    {
        var status = context.Response.StatusCode;
        var wantsHtml = HttpMethods.IsGet(context.Request.Method) &&
                        context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal);
        return wantsHtml && status is 404 or 422 or 500 ? Serve(context, status) : Task.CompletedTask;
    }

    public static async Task Serve(HttpContext context, int status)
    {
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var file = environment.WebRootFileProvider.GetFileInfo($"{status}.html");
        context.Response.StatusCode = status;
        if (file.Exists && !context.Response.HasStarted)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(file);
        }
    }
}

public partial class Program;
