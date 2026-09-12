namespace Hatch.Api.Modules.Photos;

/// <summary>
/// Everything Photos puts into DI, so the registry in ModuleRegistration stays
/// one line per app.
/// </summary>
public static class PhotosModule
{
    public static IServiceCollection AddPhotosModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleContext<PhotosContext>(configuration, PhotosContext.Schema);

        // The explicit timeout is the fail-soft convention the other outbound
        // clients keep: a slow photo server leaves the wall showing the photo
        // it already has, it never stalls a dashboard request behind it.
        // Fifteen rather than ten, because one of the calls carries a JPEG.
        services.AddHttpClient(ImmichClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));

        services.AddScoped<IImmichClient, ImmichClient>();
        services.AddScoped<IPhotoAlbumService, PhotoAlbumService>();

        // Singleton, unlike the two above: it *is* the cache. One per process,
        // holding one library, which is the whole point - see PhotoLibrary.
        services.AddSingleton<IPhotoLibrary, PhotoLibrary>();

        return services;
    }
}
