using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Gather;

/// <summary>
/// Gather's slice of the Aerie database: everything under the <c>gather</c>
/// schema, with its own migration history so a shopping-list migration never
/// touches the home-automation model. See Modules/README.md.
/// </summary>
public class GatherContext(DbContextOptions<GatherContext> options) : DbContext(options), IModuleContext
{
    public const string Schema = "gather";

    public DbSet<GatherList> Lists => Set<GatherList>();
    public DbSet<GatherItem> Items => Set<GatherItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        // A list is its items' only address: deleting "Grocery" is deleting the
        // groceries, not orphaning them somewhere nothing can reach.
        modelBuilder.Entity<GatherItem>()
            .HasOne(i => i.List)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.ListId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}
