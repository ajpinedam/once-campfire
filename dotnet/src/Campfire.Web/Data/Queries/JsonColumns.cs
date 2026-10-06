using System.Text.Json;
using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

/// <summary>accounts.settings: <c>{"restrict_room_creation_to_administrators": true}</c>.</summary>
public static class AccountSettingsJson
{
    private const string RestrictRoomCreation = "restrict_room_creation_to_administrators";

    public static AccountSettings Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return AccountSettings.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(RestrictRoomCreation, out var value)
                ? new AccountSettings(Truthy(value))
                : AccountSettings.Default;
        }
        catch (JsonException)
        {
            return AccountSettings.Default;
        }
    }

    public static string Serialize(AccountSettings settings) =>
        $$"""{"{{RestrictRoomCreation}}":{{(settings.RestrictRoomCreationToAdministrators ? "true" : "false")}}}""";

    // Form submissions historically stored "true"/"false" strings; accept every spelling.
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString() is "true" or "1",
        JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
        _ => false
    };
}

/// <summary>active_storage_blobs.metadata: <c>{"identified":true,"width":800,"height":600,"analyzed":true}</c>.</summary>
public static class BlobMetadataJson
{
    public static BlobMetadata Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return BlobMetadata.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return BlobMetadata.Empty;
            }

            return new BlobMetadata(
                Width: Int(root, "width"),
                Height: Int(root, "height"),
                Duration: root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : null,
                Analyzed: root.TryGetProperty("analyzed", out var analyzed) && analyzed.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return BlobMetadata.Empty;
        }
    }

    public static string Serialize(BlobMetadata metadata)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("identified", true);
            if (metadata.Width is { } width) writer.WriteNumber("width", width);
            if (metadata.Height is { } height) writer.WriteNumber("height", height);
            if (metadata.Duration is { } seconds) writer.WriteNumber("duration", seconds);
            writer.WriteBoolean("analyzed", metadata.Analyzed);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble())
            : null;
}
