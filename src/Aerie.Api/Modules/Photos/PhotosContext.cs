using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Photos;

/// <summary>
/// Photos' slice of the Aerie database: the <c>photos</c> schema, which holds
/// exactly one table - which albums Immich has and which of them the wall may
/// show. See Modules/README.md.
///
/// Small on purpose. Immich is the photo library; this module is a proxy and a
/// selection (docs/plans/immich.md v+2), and the day it starts keeping its own
/// copy of the library is the day there are two libraries to keep in sync.
/// </summary>
public class PhotosContext(DbContextOptions<PhotosContext> options) : DbContext(options), IModuleContext
{
    public const string Schema = "photos";

    public DbSet<PhotoAlbum> Albums => Set<PhotoAlbum>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }
}
