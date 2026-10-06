using System.Text.Json.Serialization;
using Campfire.Web.Features.Autocomplete;

namespace Campfire.Web.Features.Messages;

/// <summary>
/// Source-generated JSON for the bot API and autocomplete (jbuilder's snake_case shapes).
/// A context of its own: the System.Text.Json generator emits a whole context per partial
/// declaration carrying [JsonSerializable], so contexts can't be split across files.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MessageJson))]
[JsonSerializable(typeof(List<MessageJson>))]
[JsonSerializable(typeof(BoostJson))]
[JsonSerializable(typeof(List<AutocompletableUserJson>))]
public sealed partial class RoomsJsonContext : JsonSerializerContext;
