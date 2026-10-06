using System.Buffers.Text;
using System.Security.Cryptography;
using Campfire.Web.Configuration;
using Campfire.Web.Data;
using Campfire.Web.Security;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Admin;

/// <summary>
/// The Rails app's <c>script/admin/*</c> tools, run as <c>Campfire.Web admin &lt;command&gt;</c>:
/// <list type="bullet">
/// <item><c>generate-secrets</c> — SECRET_KEY_BASE and a VAPID key pair, as environment lines</item>
/// <item><c>create-vapid-key</c> — a VAPID key pair</item>
/// <item><c>reset-password &lt;email&gt; &lt;password&gt;</c></item>
/// <item><c>prepare-backup</c> — an online SQLite backup to storage/backups (the ONCE pre-backup hook)</item>
/// </list>
/// The first two print secrets by design: they exist so an operator can paste them into their environment.
/// </summary>
public static class AdminCommands
{
    public static int Run(IReadOnlyList<string> args, Func<CampfireSettings> settings)
    {
        switch (args)
        {
            case ["generate-secrets"]:
            {
                var (privateKey, publicKey) = GenerateVapidKey();
                Console.WriteLine($"SECRET_KEY_BASE={Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64))}");
                Console.WriteLine($"VAPID_PRIVATE_KEY={privateKey}");
                Console.WriteLine($"VAPID_PUBLIC_KEY={publicKey}");
                return 0;
            }

            case ["create-vapid-key"]:
            {
                var (privateKey, publicKey) = GenerateVapidKey();
                Console.WriteLine($"PRIVATE KEY : {privateKey}");
                Console.WriteLine($"PUBLIC KEY  : {publicKey}");
                return 0;
            }

            case ["reset-password", var emailAddress, var password]:
                return ResetPassword(settings(), emailAddress, password);

            case ["prepare-backup"]:
                return PrepareBackup(settings());

            default:
                Console.Error.WriteLine("Usage: Campfire.Web admin <generate-secrets | create-vapid-key | reset-password <email-address> <password> | prepare-backup>");
                return 64;
        }
    }

    /// <summary>A P-256 key pair in the web-push gem's encoding: raw private scalar and uncompressed public point, URL-safe base64.</summary>
    public static (string PrivateKey, string PublicKey) GenerateVapidKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        var publicPoint = new byte[65];
        publicPoint[0] = 0x04;
        parameters.Q.X!.CopyTo(publicPoint, 1);
        parameters.Q.Y!.CopyTo(publicPoint, 33);
        return (Base64Url.EncodeToString(parameters.D!), Base64Url.EncodeToString(publicPoint));
    }

    private static int ResetPassword(CampfireSettings settings, string emailAddress, string password)
    {
        var database = new Database(settings.DatabasePath);
        Schema.Migrate(database);
        using var sql = database.Open();
        var updated = sql.Execute("UPDATE users SET password_digest = @digest, updated_at = @now WHERE email_address = @email",
            ("@digest", Passwords.Hash(password)), ("@now", SqlTime.UtcNow()), ("@email", emailAddress));

        if (updated == 0)
        {
            Console.WriteLine("User not found");
            return 1;
        }

        Console.WriteLine("Password has been reset");
        return 0;
    }

    private static int PrepareBackup(CampfireSettings settings)
    {
        if (!File.Exists(settings.DatabasePath))
        {
            Console.Error.WriteLine($"No database at {settings.DatabasePath}");
            return 1;
        }

        var backups = Path.Combine(settings.StoragePath, "backups");
        Directory.CreateDirectory(backups);
        var destination = Path.Combine(backups, Path.GetFileName(settings.DatabasePath));

        using var source = new SqliteConnection($"Data Source={settings.DatabasePath};Mode=ReadWrite;Pooling=False");
        using var target = new SqliteConnection($"Data Source={destination};Pooling=False");
        source.Open();
        target.Open();
        source.BackupDatabase(target);

        Console.WriteLine($"Backed up to {destination}");
        return 0;
    }
}
