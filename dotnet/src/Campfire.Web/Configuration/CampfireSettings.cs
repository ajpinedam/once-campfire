using System.Security.Cryptography;

namespace Campfire.Web.Configuration;

/// <summary>
/// Immutable, process-wide settings resolved once at startup from the environment.
/// Mirrors the Rails app's ENV-driven configuration (SECRET_KEY_BASE, VAPID_*, APP_VERSION, ...).
/// </summary>
public sealed record CampfireSettings(
    string StoragePath,
    string DatabasePath,
    string FilesPath,
    byte[] SecretKeyBase,
    string? VapidPublicKey,
    string? VapidPrivateKey,
    string AppVersion,
    string? GitRevision,
    bool ForceSsl,
    bool IsDevelopment)
{
    public bool WebPushEnabled => !string.IsNullOrEmpty(VapidPublicKey) && !string.IsNullOrEmpty(VapidPrivateKey);

    public static CampfireSettings FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        // Rails keeps this in storage/; locally we use App_Data/ so the data directory can't collide
        // with the Storage/ source folder on case-insensitive filesystems. Docker sets STORAGE_PATH.
        var storage = Path.GetFullPath(configuration["STORAGE_PATH"] is { Length: > 0 } s ? s : Path.Combine(environment.ContentRootPath, "App_Data"));
        var databaseDirectory = Path.Combine(storage, "db");
        var files = Path.Combine(storage, "files");
        Directory.CreateDirectory(databaseDirectory);
        Directory.CreateDirectory(files);

        var databaseName = configuration["DATABASE_NAME"] is { Length: > 0 } dbName ? dbName : $"{environment.EnvironmentName.ToLowerInvariant()}.sqlite3";

        return new CampfireSettings(
            StoragePath: storage,
            DatabasePath: Path.Combine(databaseDirectory, databaseName),
            FilesPath: files,
            SecretKeyBase: ResolveSecretKeyBase(configuration["SECRET_KEY_BASE"], storage, environment),
            VapidPublicKey: NullIfEmpty(configuration["VAPID_PUBLIC_KEY"]),
            VapidPrivateKey: NullIfEmpty(configuration["VAPID_PRIVATE_KEY"]),
            AppVersion: NullIfEmpty(configuration["APP_VERSION"]) ?? NullIfEmpty(configuration["GIT_REVISION"]) ?? "0",
            GitRevision: NullIfEmpty(configuration["GIT_REVISION"]),
            ForceSsl: environment.IsProduction() && string.IsNullOrEmpty(configuration["DISABLE_SSL"]),
            IsDevelopment: environment.IsDevelopment());
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // In production SECRET_KEY_BASE must be provided. Elsewhere a random one is generated once and
    // kept in the storage directory (like Rails' tmp/local_secret.txt), never written to any log.
    private static byte[] ResolveSecretKeyBase(string? configured, string storage, IHostEnvironment environment)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            return System.Text.Encoding.UTF8.GetBytes(configured);
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException("SECRET_KEY_BASE must be set in production.");
        }

        var path = Path.Combine(storage, "local_secret.txt");
        if (!File.Exists(path))
        {
            var generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64));
            File.WriteAllText(path, generated);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        return System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
    }
}
