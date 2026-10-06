using Campfire.Web.Domain;
using Campfire.Web.Views;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Features.Messages;

/// <summary>messages/edit: either the attachment (deletable only) or the rich text editor.</summary>
public sealed record EditMessageModel(PageContext Page, Message Message, HtmlString? AttachmentPresentation, string? EditableBody);

/// <summary>messages/boosts/new.</summary>
public sealed record NewBoostModel(PageContext Page, Message Message);
