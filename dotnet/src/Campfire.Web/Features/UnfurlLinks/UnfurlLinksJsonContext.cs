using System.Text.Json.Serialization;

namespace Campfire.Web.Features.UnfurlLinks;

/// <summary>
/// Source-generated JSON for the unfurl response. A context of its own: the System.Text.Json
/// generator emits a whole context per partial declaration carrying [JsonSerializable], so
/// contexts can't be split across files.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UnfurledLink))]
public sealed partial class UnfurlLinksJsonContext : JsonSerializerContext;
