using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Campfire.Web.Cable;

/// <summary>
/// One WebSocket text message waiting to be sent: <c>Head + Body + Tail</c>. Broadcasts share the
/// body (serialized once) and differ only in the per-subscription head, so fan-out allocates nothing
/// per subscriber; the writer concatenates into a pooled buffer and sends a single frame.
/// </summary>
internal readonly record struct OutgoingFrame(ReadOnlyMemory<byte> Head, ReadOnlyMemory<byte> Body, ReadOnlyMemory<byte> Tail, bool IsClose = false)
{
    /// <summary>Tells the writer to finish the closing handshake once everything before it is sent.</summary>
    public static readonly OutgoingFrame Close = new(default, default, default, IsClose: true);

    public static OutgoingFrame Of(ReadOnlyMemory<byte> bytes) => new(default, bytes, default);

    public int Length => Head.Length + Body.Length + Tail.Length;
}

/// <summary>The <c>actioncable-v1-json</c> messages the server sends (ActionCable::INTERNAL).</summary>
internal static class CableFrames
{
    public const string Protocol = "actioncable-v1-json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Frames travel over a WebSocket, never inside HTML, so only JSON's own escapes are needed.
        // The default encoder would turn every < > & and non-ASCII character of a broadcast's HTML
        // into \uXXXX and roughly triple its size.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    public static readonly byte[] Welcome = """{"type":"welcome"}"""u8.ToArray();

    /// <summary>Closes the <c>{"identifier":…,"message":</c> object a broadcast's body is written into.</summary>
    public static readonly byte[] MessageTail = "}"u8.ToArray();

    public static byte[] Ping(long unixSeconds) =>
        Encoding.UTF8.GetBytes($$"""{"type":"ping","message":{{unixSeconds.ToString(CultureInfo.InvariantCulture)}}}""");

    public static byte[] Disconnect(string reason, bool reconnect) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "disconnect");
            writer.WriteString("reason", reason);
            writer.WriteBoolean("reconnect", reconnect);
            writer.WriteEndObject();
        });

    public static byte[] Confirm(string identifier) => Typed(identifier, "confirm_subscription");

    public static byte[] Reject(string identifier) => Typed(identifier, "reject_subscription");

    /// <summary><c>{"identifier":"…","message":</c> — precomputed once per subscription.</summary>
    public static byte[] MessageHead(string identifier)
    {
        var encoded = JsonString(identifier);
        var head = new byte[14 + encoded.Length + 11];
        "{\"identifier\":"u8.CopyTo(head);
        encoded.CopyTo(head.AsSpan(14));
        ",\"message\":"u8.CopyTo(head.AsSpan(14 + encoded.Length));
        return head;
    }

    /// <summary>A string as a JSON value (how turbo-rails broadcasts Turbo Stream HTML).</summary>
    public static byte[] JsonString(string value) => Write(writer => writer.WriteStringValue(value));

    public static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Typed(string identifier, string type) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("identifier", identifier);
            writer.WriteString("type", type);
            writer.WriteEndObject();
        });
}
