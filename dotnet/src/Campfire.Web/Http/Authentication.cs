using Campfire.Web.Cable;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;

namespace Campfire.Web.Http;

/// <summary>Starting and ending sessions (Rails' Authentication concern, the parts actions call).</summary>
public sealed class Authentication(AppCookies cookies, CableServer cable, ILogger<Authentication> logger)
{
    /// <summary><c>start_new_session_for(user)</c>: creates a session row and sets the cookie.</summary>
    public Session StartNewSessionFor(HttpContext context, Sql sql, User user)
    {
        var current = context.GetCurrent();
        var session = Sessions.Start(sql, user.Id, current.UserAgent, current.RemoteIp);
        cookies.WriteSessionToken(context, session.Token);
        current.Session = session;
        current.UserOrNull = user;
        current.AuthenticatedBy = AuthenticatedBy.Session;
        return session;
    }

    /// <summary><c>terminate_current_session</c>: destroys the session, clears cookies, drops cable connections.</summary>
    public void TerminateCurrentSession(HttpContext context, Sql sql)
    {
        var current = context.GetCurrent();
        if (current.Session is { } session)
        {
            Sessions.Delete(sql, session.Id);
        }

        cookies.DeleteSessionToken(context);

        if (current.UserOrNull is { } user)
        {
            try
            {
                cable.DisconnectUser(user.Id, reconnect: true);
            }
            catch (Exception error)
            {
                logger.LogWarning("Could not disconnect remote connections on sign out: {Error}", error.GetType().Name);
            }
        }

        current.Session = null;
        current.UserOrNull = null;
        current.AuthenticatedBy = AuthenticatedBy.None;
    }

    /// <summary><c>post_authenticating_url</c>.</summary>
    public string PostAuthenticatingUrl(HttpContext context) => cookies.ConsumeReturnTo(context);
}
