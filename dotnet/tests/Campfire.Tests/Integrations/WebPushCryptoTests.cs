using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Campfire.Tests.Integrations.Support;
using Campfire.Web.Push;

namespace Campfire.Tests.Integrations;

public sealed class WebPushCryptoTests
{
    // RFC 8291, Appendix A: "Push Message Encryption Example"
    private const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string ServerPublicKey = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string ServerPrivateKey = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string UserAgentPublicKey = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UserAgentPrivateKey = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string EncryptedMessage =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void Encrypts_the_rfc_8291_example_exactly()
    {
        using var server = Key(ServerPrivateKey, ServerPublicKey);
        var encrypted = WebPushEncryption.Encrypt(
            WebPushBase64.Decode(Plaintext), WebPushBase64.Decode(UserAgentPublicKey), WebPushBase64.Decode(AuthSecret), server, WebPushBase64.Decode(Salt));

        Assert.Equal(EncryptedMessage, WebPushBase64.Encode(encrypted));
    }

    [Fact]
    public void The_user_agent_decrypts_the_rfc_8291_example()
    {
        using var userAgent = Key(UserAgentPrivateKey, UserAgentPublicKey);
        var plaintext = WebPushDecryption.Decrypt(WebPushBase64.Decode(EncryptedMessage), userAgent, WebPushBase64.Decode(UserAgentPublicKey), WebPushBase64.Decode(AuthSecret));

        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Random_keys_and_salt_round_trip()
    {
        using var browser = new BrowserPushKeys();
        var payload = Encoding.UTF8.GetBytes("""{"title":"Hi 👋"}""");

        var first = WebPushEncryption.Encrypt(payload, browser.PublicKey, browser.AuthSecret);
        var second = WebPushEncryption.Encrypt(payload, browser.PublicKey, browser.AuthSecret);

        Assert.NotEqual(first, second); // fresh ephemeral key and salt every time
        Assert.Equal(payload, browser.Decrypt(first));
        Assert.Equal(payload, browser.Decrypt(second));
    }

    [Fact]
    public void Malformed_subscription_keys_are_cryptographic_errors()
    {
        using var browser = new BrowserPushKeys();
        Assert.ThrowsAny<CryptographicException>(() => WebPushEncryption.Encrypt("x"u8, browser.PublicKey.AsSpan(0, 33), browser.AuthSecret));
        Assert.ThrowsAny<CryptographicException>(() => WebPushEncryption.Encrypt("x"u8, browser.PublicKey, []));
    }

    [Fact]
    public void Vapid_token_is_an_es256_jwt_for_the_push_service_origin()
    {
        var keys = VapidTestKeys.Generate();
        using var signer = VapidSigner.Create(keys.PrivateKey, keys.PublicKey);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var header = signer.AuthorizationHeader(new Uri("https://fcm.googleapis.com/fcm/send/abc123"), now);
        Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
        Assert.EndsWith($", k={keys.PublicKey.TrimEnd('=')}", header, StringComparison.Ordinal);

        var jwt = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)].Split('.');
        Assert.Equal(3, jwt.Length);

        using var head = JsonDocument.Parse(WebPushBase64.Decode(jwt[0]));
        Assert.Equal("ES256", head.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", head.RootElement.GetProperty("typ").GetString());

        using var claims = JsonDocument.Parse(WebPushBase64.Decode(jwt[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:support@37signals.com", claims.RootElement.GetProperty("sub").GetString());
        var expires = claims.RootElement.GetProperty("exp").GetInt64();
        Assert.InRange(expires - now.ToUnixTimeSeconds(), 1, 24 * 60 * 60);

        var publicKey = WebPushBase64.Decode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] }
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), WebPushBase64.Decode(jwt[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Vapid_keys_written_by_the_ruby_gem_with_padding_are_accepted()
    {
        var keys = VapidTestKeys.Generate();
        var padded = Convert.ToBase64String(WebPushBase64.Decode(keys.PublicKey)).Replace('+', '-').Replace('/', '_');
        using var signer = VapidSigner.Create(keys.PrivateKey + "=", padded);
        Assert.Equal(keys.PublicKey, signer.PublicKey);
    }

    [Fact]
    public void Invalid_vapid_keys_are_rejected()
    {
        Assert.ThrowsAny<CryptographicException>(() => VapidSigner.Create("test-vapid-private-key", "BPh2h8Xb4K2L8hQh0N1cN2xS8D3zv0g"));
    }

    [Fact]
    public void Notification_payload_matches_the_rails_shape()
    {
        var json = new WebPushMessage("HQ", "David: Hello <b>&</b> 👋", "/rooms/1", 3, "/account/logo").ToJson();

        // Same members, in the same order, as JSON.generate(title:, options: { body:, icon:, data: { path:, badge: } })
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["title", "options"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("HQ", root.GetProperty("title").GetString());
        var options = root.GetProperty("options");
        Assert.Equal(["body", "icon", "data"], options.EnumerateObject().Select(property => property.Name));
        Assert.Equal("David: Hello <b>&</b> 👋", options.GetProperty("body").GetString());
        Assert.Equal("/account/logo", options.GetProperty("icon").GetString());
        Assert.Equal("/rooms/1", options.GetProperty("data").GetProperty("path").GetString());
        Assert.Equal(3, options.GetProperty("data").GetProperty("badge").GetInt32());
        Assert.Contains("<b>&</b>", Encoding.UTF8.GetString(json), StringComparison.Ordinal); // not HTML-escaped
    }

    [Fact]
    public void Long_notification_bodies_are_shortened_to_fit_one_record()
    {
        var json = new WebPushMessage("HQ", new string('é', 5000), "/rooms/1", 0, "/account/logo").ToJson();
        Assert.InRange(json.Length, WebPushEncryption.MaxPlaintextSize - 16, WebPushEncryption.MaxPlaintextSize);

        var emoji = new WebPushMessage("HQ", string.Concat(Enumerable.Repeat("🔥", 3000)), "/rooms/1", 0, "/account/logo").ToJson();
        Assert.InRange(emoji.Length, 1, WebPushEncryption.MaxPlaintextSize);
        using var parsed = JsonDocument.Parse(emoji); // never cut inside a surrogate pair
        using var document = JsonDocument.Parse(json);
        Assert.EndsWith("…", document.RootElement.GetProperty("options").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    private static ECDiffieHellman Key(string privateKey, string publicKey)
    {
        var q = WebPushBase64.Decode(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebPushBase64.Decode(privateKey),
            Q = new ECPoint { X = q[1..33], Y = q[33..] }
        });
    }
}
