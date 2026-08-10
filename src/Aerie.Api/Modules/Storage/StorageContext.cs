using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Storage;

/// <summary>
/// Storage Helper's slice of the Aerie database: everything under the
/// <c>storage</c> schema, with its own migration history so a crate migration
/// never touches the home-automation model. See Modules/README.md.
/// </summary>
public class StorageContext(DbContextOptions<StorageContext> options) : DbContext(options), IModuleContext
{
    public const string Schema = "storage";

    public DbSet<StorageLocation> Locations => Set<StorageLocation>();
    public DbSet<Crate> Crates => Set<Crate>();
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        // A location is where crates happen to be, not what they belong to:
        // deleting "Attic" leaves its crates as unplaced rather than deleting
        // the record of what's in them.
        modelBuilder.Entity<Crate>()
            .HasOne(c => c.Location)
            .WithMany(l => l.Crates)
            .HasForeignKey(c => c.LocationId)
            .OnDelete(DeleteBehavior.SetNull);

        // A crate, by contrast, is its items' only address - they go with it.
        modelBuilder.Entity<Item>()
            .HasOne(i => i.Crate)
            .WithMany(c => c.Items)
            .HasForeignKey(i => i.CrateId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>Lets `dotnet ef --context StorageContext` build the model without running Program.cs.</summary>
public class StorageDesignTimeFactory : ModuleDesignTimeFactory<StorageContext>
{
    protected override string Schema => StorageContext.Schema;
}
