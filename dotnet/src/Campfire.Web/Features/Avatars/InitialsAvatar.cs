using System.Globalization;
using System.Text;
using Campfire.Web.Domain;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Avatars;

/// <summary>users/avatars/show.svg.erb: a colored square with the user's initials.</summary>
public static class InitialsAvatar
{
    private static readonly string[] Colors =
    [
        "#AF2E1B", "#CC6324", "#3B4B59", "#BFA07A", "#ED8008", "#ED3F1C", "#BF1B1B", "#736B1E", "#D07B53",
        "#736356", "#AD1D1D", "#BF7C2A", "#C09C6F", "#698F9C", "#7C956B", "#5D618F", "#3B3633", "#67695E"
    ];

    /// <summary>Rails' <c>avatar_background_color</c>: <c>Zlib.crc32(user.to_param) % colors</c>, stable per user.</summary>
    public static string BackgroundColor(long userId) =>
        Colors[Crc32(Encoding.ASCII.GetBytes(userId.ToString(CultureInfo.InvariantCulture))) % (uint)Colors.Length];

    public static string Render(User user)
    {
        var initials = user.Initials;
        var fit = initials.Length >= 3 ? " textLength=\"85%\" lengthAdjust=\"spacingAndGlyphs\"" : "";
        return $"""
            <svg version="1.1" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"
              viewBox="0 0 512 512" class="avatar" aria-hidden="true">
              <defs>
                <clipPath id="porthole">
                  <circle cx="50%" cy="50%" r="50%" />
                </clipPath>
              </defs>

              <g>
                <rect width="100%" height="100%" rx="50" fill="{BackgroundColor(user.Id)}" />

                <text x="50%" y="50%" fill="#FFFFFF"
                  text-anchor="middle" dy="0.35em"
                 {fit}
                  font-family="-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica, Arial, sans-serif"
                  font-size="230"
                  font-weight="800"
                  letter-spacing="-5">
                  {MinimalHtmlEncoder.Escape(initials)}
                </text>
              </g>
            </svg>
            """;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
