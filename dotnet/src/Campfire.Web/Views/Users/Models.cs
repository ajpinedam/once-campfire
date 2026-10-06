using Campfire.Web.Domain;

namespace Campfire.Web.Views.Users;

/// <summary>users/new: signing up through the account's join link.</summary>
public sealed record JoinModel(PageContext Page, string JoinCode, User? HelpContact);

/// <summary>users/show.</summary>
public sealed record UserShowModel(PageContext Page, User User, TransferModel? Transfer);

/// <summary>users/profiles/_transfer: the auto-login link for another device.</summary>
public sealed record TransferModel(string Url, bool ForSomeoneElse);
