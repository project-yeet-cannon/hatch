using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Hatch's slice of the Aerie database: the <c>hatch</c> schema, its own
/// migration history, six tables.
/// </summary>
public class HatchContext(DbContextOptions<HatchContext> options) : DbContext(options), IModuleContext
{
    public const string Schema = "hatch";

    public DbSet<EfHatchProject> Projects => Set<EfHatchProject>();
    public DbSet<EfHatchStatus> Statuses => Set<EfHatchStatus>();
    public DbSet<EfHatchIssue> Issues => Set<EfHatchIssue>();
    public DbSet<EfHatchComment> Comments => Set<EfHatchComment>();
    public DbSet<EfHatchIssueEvent> IssueEvents => Set<EfHatchIssueEvent>();
    public DbSet<EfHatchPlaybook> Playbooks => Set<EfHatchPlaybook>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var issue = modelBuilder.Entity<EfHatchIssue>();

        // Restrict, not cascade: a project or a status is deleted through an
        // endpoint that first refuses while anything still points at it (409
        // with a reason). Cascade here would turn that considered refusal into
        // a delete that silently takes the board's contents with it - and the
        // guard is the kind of thing a future code path can forget, while the
        // constraint is not.
        issue.HasOne(i => i.Project)
            .WithMany(p => p.Issues)
            .HasForeignKey(i => i.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        issue.HasOne(i => i.Status)
            .WithMany()
            .HasForeignKey(i => i.StatusId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting an epic orphans its stories rather than deleting them.
        // Losing a parent is an outdent; losing an epic should not quietly take
        // eleven stories off the board.
        issue.HasOne(i => i.Parent)
            .WithMany(i => i.Children)
            .HasForeignKey(i => i.ParentId)
            .OnDelete(DeleteBehavior.SetNull);

        // Comments and events, on the other hand, have no meaning without their
        // issue - and this is the accepted MVP gap written down in the plan: a
        // deleted issue takes its audit trail with it.
        modelBuilder.Entity<EfHatchComment>()
            .HasOne(c => c.Issue)
            .WithMany(i => i.Comments)
            .HasForeignKey(c => c.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfHatchIssueEvent>(e =>
        {
            e.HasOne(x => x.Issue)
                .WithMany(i => i.Events)
                .HasForeignKey(x => x.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            // jsonb rather than text: the payload shape differs per kind, and
            // the day someone asks "which issues moved out of review twice"
            // that is a query against this column rather than a migration.
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        // Restrict for the same reason issues restrict: a column is deleted
        // through an endpoint that refuses while anything still points at it,
        // and cascade here would let deleting "review" quietly take the
        // instructions for reaching it as well.
        modelBuilder.Entity<EfHatchPlaybook>(e =>
        {
            e.HasOne(p => p.FromStatus)
                .WithMany()
                .HasForeignKey(p => p.FromStatusId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(p => p.ToStatus)
                .WithMany()
                .HasForeignKey(p => p.ToStatusId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }
}
