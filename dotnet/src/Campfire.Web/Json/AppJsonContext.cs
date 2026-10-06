using System.Text.Json.Serialization;

namespace Campfire.Web.Json;

/// <summary>
/// Source-generated JSON metadata (snake_case, like jbuilder output). Each module adds its own
/// DTOs with <c>[JsonSerializable(typeof(...))]</c> on another <c>partial</c> declaration of this
/// class in its own folder; the generator merges them.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Dictionary<string, object>))]
public sealed partial class AppJsonContext : JsonSerializerContext;
