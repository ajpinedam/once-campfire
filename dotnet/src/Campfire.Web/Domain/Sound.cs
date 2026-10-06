using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Campfire.Web.Domain;

public sealed record SoundImage(string AssetPath, int Width, int Height);

/// <summary>A <c>/play name</c> sound: an mp3 plus either an image or a line of text to show.</summary>
public sealed partial record Sound(string Name, string? Text, SoundImage? Image)
{
    public string AssetPath => $"{Name}.mp3";

    private static Sound WithText(string name, string text) => new(name, text, null);
    private static Sound WithImage(string name, string image, int width, int height) => new(name, null, new SoundImage($"sounds/{image}", width, height));

    public static readonly IReadOnlyList<Sound> Builtin =
    [
        WithImage("56k", "56k.webp", 79, 33),
        WithText("bell", "🔔"),
        WithText("bezos", "😆💭"),
        WithText("bueller", "anyone?"),
        WithText("butts", "👐 🚬"),
        WithImage("clowntown", "clowntown.webp", 210, 150),
        WithText("cottoneyejoe", "🎶🙉🎶 "),
        WithText("crickets", "hears crickets chirping"),
        WithImage("curb", "curb.webp", 150, 101),
        WithText("dadgummit", "dad gummit!! 🎣"),
        WithImage("dangerzone", "dangerzone.webp", 157, 32),
        WithText("danielsan", "🎆 🏆 🎆"),
        WithImage("deeper", "top.webp", 188, 80),
        WithText("ballmer", "developers!"),
        WithImage("donotwant", "donotwant.webp", 150, 150),
        WithImage("drama", "drama.webp", 300, 16),
        WithText("flawless", "#flawless"),
        WithText("glados", "🤖💢"),
        WithText("gogogo", "Go, go, go!"),
        WithImage("greatjob", "greatjob.webp", 79, 16),
        WithText("greyjoy", "😖🎺"),
        WithText("guarantee", "guarantees it 👌"),
        WithText("heygirl", "✨💁✨"),
        WithText("honk", "HONK"),
        WithText("horn", "🐶 ✂️ 🐱"),
        WithText("horror", "💀 💀 💀 💀 💀 💀 💀"),
        WithText("inconceivable", "doesn't think it means what you think it means…"),
        WithText("letitgo", "❄️👩❄️⛄️❄️"),
        WithText("live", "is DOING IT LIVE"),
        WithImage("loggins", "loggins.webp", 200, 151),
        WithText("makeitso", "make it so 👉"),
        WithText("noooo", "👸💀😒"),
        WithImage("nyan", "nyan.webp", 36, 15),
        WithText("ohmy", "raises an eyebrow 😏"),
        WithText("ohyeah", "isn't playing by the rules"),
        WithImage("pushit", "pushit.webp", 104, 15),
        WithText("rimshot", "plays a rimshot"),
        WithText("rollout", "is rolling out 🚗"),
        WithImage("rumble", "rumble.webp", 220, 150),
        WithText("sax", "🌇🎷🎶"),
        WithText("secret", "found a secret area 🔑"),
        WithText("sexyback", "🔞"),
        WithText("story", "and now you know…"),
        WithText("tada", "plays a fanfare 🎏"),
        WithText("tmyk", "✨ ⭐️ The More You Know ✨ ⭐️"),
        WithText("totes", "😁👍"),
        WithText("trololo", "трололо"),
        WithText("trombone", "plays a sad trombone"),
        WithText("unix", "knows this 💻"),
        WithText("vuvuzela", "======<() ~ ♪ ~♫"),
        WithImage("what", "what.webp", 100, 131),
        WithText("whoomp", "👏‼️😎"),
        WithText("wups", "wups!"),
        WithImage("yay", "yay.webp", 103, 50),
        WithImage("yeah", "yeah.webp", 104, 15),
        WithText("yodel", "📣🗻🙉")
    ];

    private static readonly FrozenDictionary<string, Sound> Index = Builtin.ToFrozenDictionary(sound => sound.Name, StringComparer.Ordinal);

    public static IReadOnlyList<string> Names { get; } = [.. Index.Keys.Order(StringComparer.Ordinal)];

    public static Sound? FindByName(string name) => Index.GetValueOrDefault(name);

    /// <summary>A message is a sound when its plain text is exactly <c>/play name</c> for a known sound.</summary>
    public static Sound? FromPlainText(string plainText)
    {
        var match = PlayCommand().Match(plainText);
        return match.Success ? FindByName(match.Groups["name"].Value) : null;
    }

    [GeneratedRegex(@"\A/play (?<name>\w+)\z")]
    private static partial Regex PlayCommand();
}
