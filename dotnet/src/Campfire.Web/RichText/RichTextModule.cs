namespace Campfire.Web.RichText;

// CONTRACT STUB — owned by the RichText workstream.

public static class RichTextModule
{
    public static IServiceCollection AddRichTextModule(this IServiceCollection services)
    {
        services.AddSingleton<RichTextService>();
        return services;
    }
}
