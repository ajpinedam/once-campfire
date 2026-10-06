using Campfire.Web.Domain;
using Campfire.Web.Platform;

namespace Campfire.Web.Views.PushSubscriptions;

/// <summary>Model for <c>Views/PushSubscriptions/Index.cshtml</c> (users/push_subscriptions/index).</summary>
public sealed record PushSubscriptionsPage(PageContext Page, IReadOnlyList<PushSubscription> Subscriptions, string BackPath)
{
    /// <summary><c>"#{agent.browser} #{agent.version} on #{agent.platform}"</c>, as the Rails view described each device.</summary>
    public static string DescribeAgent(string? userAgent)
    {
        var agent = ApplicationPlatform.Parse(userAgent);
        var version = agent.BrowserVersion is { } v ? v.ToString() : "";
        return $"{agent.Browser} {version} on {agent.PlatformName}";
    }
}
