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

        // Search documents are Postgres's job: a STORED generated column, so an
        // item's text is tokenised on write and no code path can forget to
        // reindex after an edit.
        //
        // The GIN index covers that column alone. StorageController.Search
        // concatenates each row's crate and location text onto it - a generated
        // column can only see its own row - and a concatenated vector can't use
        // the index, so that query scans. At a few hundred items that is the
        // right trade against the alternative (splitting the query per matched
        // crate, or a trigger-maintained denormalised document); the index is
        // here for any predicate that is about an item's own text, and it is what
        // stops "make search index-backed" from being a schema change later.
        //
        // Guarded by provider because a tsvector has no equivalent on EF's
        // InMemory provider, which the module's unit tests run on - it refuses to
        // map the property at all. Search itself is a database feature and is
        // verified against real Postgres; everything those tests do cover (the
        // delete behaviours, crate minting) is provider-agnostic and stays that way.
        if (Database.IsNpgsql())
        {
            modelBuilder.Entity<Item>()
                .HasGeneratedTsVectorColumn(i => i.SearchVector, SearchQuery.Config, i => new { i.Name, i.Notes })
                .HasIndex(i => i.SearchVector)
                .HasMethod("GIN");
        }
        else
        {
            modelBuilder.Entity<Item>().Ignore(i => i.SearchVector);
        }

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>Lets `dotnet ef --context StorageContext` build the model without running Program.cs.</summary>
public class StorageDesignTimeFactory : ModuleDesignTimeFactory<StorageContext>
{
    protected override string Schema => StorageContext.Schema;
}
