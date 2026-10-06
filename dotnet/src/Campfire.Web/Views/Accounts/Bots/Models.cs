using Campfire.Web.Domain;

namespace Campfire.Web.Views.Accounts.Bots;

/// <summary>accounts/bots/index.</summary>
public sealed record BotsIndexModel(PageContext Page, IReadOnlyList<BotEntry> Bots);

/// <summary>accounts/bots/_bot: a bot and the shared rooms it can post to.</summary>
public sealed record BotEntry(PageContext Page, User Bot, IReadOnlyList<Room> Rooms);

/// <summary>accounts/bots/new and edit (and their _form).</summary>
public sealed record BotFormModel(PageContext Page, User? Bot, string? WebhookUrl, bool HasAvatar);
