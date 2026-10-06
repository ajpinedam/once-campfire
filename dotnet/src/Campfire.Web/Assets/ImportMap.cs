using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Campfire.Web.Assets;

/// <summary>
/// config/importmap.rb, resolved against the digested assets. Rendered once; every page embeds
/// the same <c>&lt;script type="importmap"&gt;</c>, modulepreload links and entry point.
/// </summary>
public sealed class ImportMap
{
    public ImportMap(AssetCatalog assets)
    {
        var pins = new SortedDictionary<string, string>(StringComparer.Ordinal);

        void Pin(string name, string logicalPath) => pins[name] = assets.PathFor(logicalPath);

        void PinAllFrom(string directory, string under)
        {
            foreach (var path in assets.LogicalPaths.Where(p => p.StartsWith(directory + "/", StringComparison.Ordinal) && p.EndsWith(".js", StringComparison.Ordinal)))
            {
                var module = under + path[directory.Length..^".js".Length];
                pins[module.EndsWith("/index", StringComparison.Ordinal) ? module[..^"/index".Length] : module] = assets.PathFor(path);
            }
        }

        Pin("application", "application.js");
        Pin("@hotwired/stimulus", "stimulus.min.js");
        Pin("@hotwired/stimulus-loading", "stimulus-loading.js");
        Pin("@hotwired/turbo-rails", "turbo.min.js");
        Pin("@rails/actioncable", "actioncable.esm.js");
        Pin("@rails/request.js", "@rails--request.js");
        Pin("lexxy", "lexxy.min.js");
        Pin("highlight.js", "highlight.js/core.js");

        PinAllFrom("initializers", "initializers");
        PinAllFrom("lib", "lib");
        PinAllFrom("channels", "channels");
        PinAllFrom("controllers", "controllers");
        PinAllFrom("helpers", "helpers");
        PinAllFrom("models", "models");
        PinAllFrom("languages", "languages");

        Html = Render(pins);
    }

    /// <summary>The complete <c>javascript_importmap_tags</c> output.</summary>
    public string Html { get; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Render(SortedDictionary<string, string> pins)
    {
        var json = JsonSerializer.Serialize(new { imports = pins }, JsonOptions);

        var html = new StringBuilder();
        html.Append("<script type=\"importmap\" data-turbo-track=\"reload\">").Append(json).Append("</script>\n");
        foreach (var url in pins.Values.Distinct())
        {
            html.Append("<link rel=\"modulepreload\" href=\"").Append(HtmlEncoder.Default.Encode(url)).Append("\">\n");
        }
        html.Append("<script type=\"module\">import \"application\"</script>");
        return html.ToString();
    }
}
