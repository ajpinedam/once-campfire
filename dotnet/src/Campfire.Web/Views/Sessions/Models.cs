using Campfire.Web.Domain;

namespace Campfire.Web.Views.Sessions;

/// <summary>sessions/new.</summary>
public sealed record SignInModel(PageContext Page, string? EmailAddress, User? HelpContact);

/// <summary>sessions/transfers/show: a form that submits itself to claim the transfer link.</summary>
public sealed record TransferShowModel(PageContext Page, string Action);

/// <summary>sessions/incompatible_browser.</summary>
public sealed record IncompatibleBrowserModel(PageContext Page);
