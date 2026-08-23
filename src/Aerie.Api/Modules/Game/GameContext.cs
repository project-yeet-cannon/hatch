using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Game;

/// <summary>
/// The game module's slice of the Aerie database: everything under the
/// <c>game</c> schema, with its own migration history. See Modules/README.md.
/// </summary>
public class GameContext(DbContextOptions<GameContext> options) : DbContext(options), IModuleContext
{
    public const string Schema = "game";

    public DbSet<GameWorld> Worlds => Set<GameWorld>();
    public DbSet<GameVersion> Versions => Set<GameVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        // A version has no meaning away from its world: deleting a game deletes
        // everything it was ever made of.
        modelBuilder.Entity<GameVersion>()
            .HasOne(v => v.World)
            .WithMany(w => w.Versions)
            .HasForeignKey(v => v.WorldId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}
