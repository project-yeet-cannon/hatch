using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Hatch's slice of the Aerie database: the <c>hatch</c> schema, its own
/// migration history, eight tables.
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
    public DbSet<EfHatchIssueDependency> Dependencies => Set<EfHatchIssueDependency>();
    public DbSet<EfHatchWorkLogEntry> WorkLog => Set<EfHatchWorkLogEntry>();

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
        modelBuilder.Entity<EfHatchComment>(e =>
        {
            e.HasOne(c => c.Issue)
                .WithMany(i => i.Comments)
                .HasForeignKey(c => c.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            // An answer points at the question it settles, on the same issue.
            //
            // NoAction rather than the Restrict used everywhere else, and the
            // difference is load-bearing here: deleting an issue cascades to
            // every comment on it, question and answer together, and PostgreSQL
            // checks a RESTRICT immediately - the answer row would be seen
            // pointing at a question mid-delete and the whole delete would fail.
            // NO ACTION is checked once the statement is finished, by which
            // point both rows have gone and there is nothing to complain about.
            e.HasOne(c => c.Answers)
                .WithMany(c => c.AnsweredBy)
                .HasForeignKey(c => c.AnswersId)
                .OnDelete(DeleteBehavior.NoAction);

            // jsonb for the reason the event payload is - see
            // EfHatchComment.Options.
            e.Property(c => c.Options).HasColumnType("jsonb");
        });

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

        // Cascade from both ends, which is the one place in this module where
        // that is obviously right: an edge has no meaning without either issue,
        // so deleting either takes the edges pointing at it in both directions.
        // No inverse navigation on the issue for either end - two collections
        // told apart only by name, and nothing reads them.
        modelBuilder.Entity<EfHatchIssueDependency>(e =>
        {
            e.HasOne(d => d.Issue)
                .WithMany()
                .HasForeignKey(d => d.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(d => d.DependsOn)
                .WithMany()
                .HasForeignKey(d => d.DependsOnId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Cascade with the issue, the way comments and events do and for the
        // same reason: a meter reading for a ticket nobody kept is a row about
        // nothing. What the account spent in total is a separate record and
        // does not depend on this one surviving.
        modelBuilder.Entity<EfHatchWorkLogEntry>(e =>
        {
            e.HasOne(w => w.Issue)
                .WithMany(i => i.WorkLog)
                .HasForeignKey(w => w.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            // Eight places, because a short session costs a fraction of a cent
            // and the default scale would round a night of them to nothing.
            e.Property(w => w.CostUsd).HasPrecision(18, 8);

            // jsonb for the reason the event payload is - see
            // EfHatchWorkLogEntry.ModelUsage.
            e.Property(w => w.ModelUsage).HasColumnType("jsonb");
        });

        base.OnModelCreating(modelBuilder);
    }
}
