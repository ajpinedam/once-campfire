namespace Campfire.Web.Storage;

public static class StorageModule
{
    public static IServiceCollection AddStorageModule(this IServiceCollection services)
    {
        services.AddSingleton<BlobStore>();
        return services;
    }

    /// <summary>Endpoints serving blob files and variants.</summary>
    public static IEndpointRouteBuilder MapStorageModule(this IEndpointRouteBuilder app)
    {
        // Constructing the store configures BlobUrls' signer and libvips' safety settings before
        // the first request (or the first view) needs them.
        app.ServiceProvider.GetRequiredService<BlobStore>();
        return app.MapStorageEndpoints();
    }
}
