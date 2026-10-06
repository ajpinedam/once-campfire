using Campfire.Web.Configuration;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Security;

namespace Campfire.Web.Http;

/// <summary>
/// Everything Rails' ApplicationController did before an action, in the same order:
/// version headers, allow_browser, require_authentication (session cookie or bot key),
/// deny_bots, CSRF verification, and rejecting writes from banned IP addresses.
/// Runs after routing so it can read each endpoint's <see cref="AccessPolicy"/>.
/// </summary>
public sealed class CampfireRequestMiddleware(
    RequestDelegate next,
    CampfireSettings settings,
    AccountCache accounts,
    AppCookies cookies,
    Csrf csrf,
    ILogger<CampfireRequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            await next(context);
            return;
        }

        var policy = endpoint.Metadata.GetMetadata<AccessPolicy>() ?? AccessPolicy.Default;
        var sql = context.RequestServices.GetRequiredService<Sql>(); // opens a connection only if queried
        var current = new Current(context, accounts.Get(sql));
        context.SetCurrent(current);

        context.Response.Headers["X-Version"] = settings.AppVersion;
        if (settings.GitRevision is { } revision)
        {
            context.Response.Headers["X-Rev"] = revision;
        }

        if (!policy.SkipBrowserCheck && current.Platform.IsUnsupportedBrowser)
        {
            await Features.Sessions.IncompatibleBrowser.Render(context);
            return;
        }

        if (!Authenticate(context, current, policy, sql))
        {
            return;
        }

        if (current.AuthenticatedBy == AuthenticatedBy.BotKey && !policy.AllowBots)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);

        if (unsafeMethod && !policy.SkipCsrf && current.AuthenticatedBy != AuthenticatedBy.BotKey && !await VerifiedRequest(context))
        {
            logger.LogWarning("Can't verify CSRF token authenticity for {Method} {Path}", context.Request.Method, LogScrubbing.Path(context.Request.Path));
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return;
        }

        if (unsafeMethod && Bans.IsBanned(sql, current.RemoteIp))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        await next(context);
    }

    /// <returns>false when the request has been answered (redirected) and must stop here.</returns>
    private bool Authenticate(HttpContext context, Current current, AccessPolicy policy, Sql sql)
    {
        if (policy.RequireUnauthenticated)
        {
            if (RestoreSession(context, current, sql))
            {
                context.Response.Redirect("/");
                return false;
            }
            return true;
        }

        if (policy.AllowUnauthenticated)
        {
            return true;
        }

        if (RestoreSession(context, current, sql) || BotAuthentication(context, current, sql))
        {
            return true;
        }

        // request_authentication: remember where they were going, then send them to sign in
        cookies.SetReturnTo(context, $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}");
        context.Response.Redirect("/session/new");
        return false;
    }

    private bool RestoreSession(HttpContext context, Current current, Sql sql)
    {
        if (cookies.ReadSessionToken(context) is not { } token || Sessions.FindWithUser(sql, token) is not { } found)
        {
            return false;
        }

        var (session, user) = found;
        Sessions.Resume(sql, session, current.UserAgent, current.RemoteIp);
        current.Session = session;
        current.UserOrNull = user;
        current.AuthenticatedBy = AuthenticatedBy.Session;
        return true;
    }

    private static bool BotAuthentication(HttpContext context, Current current, Sql sql)
    {
        if (context.Request.RouteValues["bot_key"] is not string { Length: > 0 } botKey ||
            Users.AuthenticateBot(sql, botKey.Trim()) is not { } bot)
        {
            return false;
        }

        current.UserOrNull = bot;
        current.AuthenticatedBy = AuthenticatedBy.BotKey;
        return true;
    }

    private async Task<bool> VerifiedRequest(HttpContext context)
    {
        if (csrf.IsValid(context, context.Request.Headers[Csrf.HeaderName]))
        {
            return true;
        }

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return csrf.IsValid(context, form[Csrf.FormFieldName]);
        }

        return false;
    }
}
