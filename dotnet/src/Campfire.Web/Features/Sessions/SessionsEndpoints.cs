using System.Threading.RateLimiting;
using Campfire.Web.Data;
using Campfire.Web.Http;
using Campfire.Web.Views;
using Campfire.Web.Views.Sessions;
using Microsoft.AspNetCore.RateLimiting;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.Sessions;

/// <summary>SessionsController and Sessions::TransfersController.</summary>
public static class SessionsEndpoints
{
    public const string RateLimitPolicy = "sessions-create";
    private const string RejectionAlert = "Too many requests or unauthorized.";

    /// <summary>Signing in from the same address: 10 tries per 3 minutes (Rails' <c>rate_limit to: 10, within: 3.minutes</c>).</summary>
    public static IServiceCollection AddSessionsRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(limiter => limiter.AddPolicy(RateLimitPolicy, new SignInRateLimitPolicy()));

    public static IEndpointRouteBuilder MapSessions(this IEndpointRouteBuilder app)
    {
        app.MapGet(Paths.NewSession, New).AllowUnauthenticated();
        app.MapPost(Paths.Session, Create).AllowUnauthenticated().RequireRateLimiting(RateLimitPolicy);
        app.MapDelete(Paths.Session, Destroy);

        app.MapGet("/session/transfers/{id}", ShowTransfer).AllowUnauthenticated();
        app.MapMethods("/session/transfers/{id}", ["PUT", "PATCH"], UpdateTransfer).AllowUnauthenticated();
        return app;
    }

    private static IResult New(HttpContext context, Sql sql, string? email_address)
    {
        if (Q.Users.None(sql))
        {
            return Respond.Redirect(context, Paths.FirstRun);
        }

        return SignInPage(context, sql, email_address, StatusCodes.Status200OK);
    }

    private static async Task<IResult> Create(HttpContext context, Sql sql, Authentication authentication)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var emailAddress = form["email_address"].ToString();

        if (Q.Users.Authenticate(sql, emailAddress, form["password"]) is { } user)
        {
            authentication.StartNewSessionFor(context, sql, user);
            return Respond.Redirect(context, authentication.PostAuthenticatingUrl(context));
        }

        return RenderRejection(context, sql, StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> Destroy(HttpContext context, Sql sql, Current current, Authentication authentication)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["push_subscription_endpoint"].ToString() is { Length: > 0 } endpoint)
            {
                Q.PushSubscriptions.DeleteByEndpoint(sql, current.User.Id, endpoint);
            }
        }

        authentication.TerminateCurrentSession(context, sql);
        return Respond.Redirect(context, Paths.Root);
    }

    private static RazorSlices.RazorSlice<Views.Sessions.TransferShowModel> ShowTransfer(HttpContext context, string id) =>
        Transfer.Create(new TransferShowModel(PageContext.For(context), Paths.SessionTransfer(id)));

    private static IResult UpdateTransfer(HttpContext context, Sql sql, Security.KeyRing keys, Authentication authentication, string id)
    {
        if (keys.SignedIds.Find(id, TransferIds.Purpose) is { } userId && Q.Users.FindActive(sql, userId) is { } user)
        {
            authentication.StartNewSessionFor(context, sql, user);
            return Respond.Redirect(context, authentication.PostAuthenticatingUrl(context));
        }

        return Respond.Head(StatusCodes.Status400BadRequest);
    }

    /// <summary><c>render_rejection</c>: the sign-in page again, with an alert and a failing status.</summary>
    internal static IResult RenderRejection(HttpContext context, Sql sql, int status)
    {
        AppCookies.SetFlashNow(context, alert: RejectionAlert);
        return SignInPage(context, sql, null, status);
    }

    private static IResult SignInPage(HttpContext context, Sql sql, string? emailAddress, int status) =>
        Results.RazorSlice<SignIn, SignInModel>(new SignInModel(PageContext.For(context), emailAddress, Q.Users.FirstAdministrator(sql)), status);

    /// <summary>
    /// A fixed window per remote address. Rejections render the sign-in page (status 429); the
    /// limiter runs before CampfireRequestMiddleware, so the request state is set up here.
    /// </summary>
    private sealed class SignInRateLimitPolicy : IRateLimiterPolicy<string>
    {
        public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
            RateLimitPartition.GetFixedWindowLimiter(Current.RemoteIpOf(httpContext) ?? "unknown", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(3),
                QueueLimit = 0
            });

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = async (rejected, _) =>
        {
            var context = rejected.HttpContext;
            var sql = context.RequestServices.GetRequiredService<Sql>();
            if (context.TryGetCurrent() is null)
            {
                context.SetCurrent(new Current(context, context.RequestServices.GetRequiredService<AccountCache>().Get(sql)));
            }

            await RenderRejection(context, sql, StatusCodes.Status429TooManyRequests).ExecuteAsync(context);
        };
    }
}

/// <summary>Rails' <c>User::Transferable</c>: signed links that sign someone in on another device.</summary>
public static class TransferIds
{
    public const string Purpose = "transfer";
    public static readonly TimeSpan ExpiresIn = TimeSpan.FromHours(4);

    public static string For(Security.KeyRing keys, long userId) => keys.SignedIds.Generate(userId, Purpose, ExpiresIn);
}
