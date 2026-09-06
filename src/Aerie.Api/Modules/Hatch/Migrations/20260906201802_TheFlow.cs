using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <summary>
    /// The flow every install starts with: the operator's columns and the
    /// agent's, in the order that makes an unattended loop safe.
    ///
    /// <code>
    ///   Draft       operator   an idea being written; nothing reads it
    ///   Breakdown   agent      turn the draft into a specification
    ///   Backlog     operator   specified work, awaiting selection
    ///   To Do       agent      pick it up and do it
    ///   In Progress agent      finish it, push it, put it up for review
    ///   In Review   operator   read the pull request, wait for green, merge
    ///   Done        operator   terminal
    /// </code>
    ///
    /// The four columns <c>Init</c> seeded plus the <c>review</c> that
    /// <c>Playbooks</c> measured into place are a board with three agent
    /// columns in a row and no operator gate between "somebody wrote a
    /// paragraph" and "a pull request is open". That was fine while a person
    /// ran every increment by hand. It is not fine for a loop, and a second
    /// operator cloning Aerie would have had to arrange this board themselves
    /// before <c>go-to-work</c> had anywhere sensible to run - which is exactly
    /// the thing docs/ethos.md says may not be true of one installation.
    ///
    /// **Which column belongs to whom is not stated here, and must not be.** It
    /// is derived: a column an agent may leave is a column some playbook names
    /// as its <c>from</c> for that issue's type. This migration adds the two
    /// missing columns and puts the playbooks that already exist on the
    /// transitions they now describe. It writes no prompt, no model and no
    /// effort, so an operator's tuning survives it untouched.
    /// </summary>
    /// <inheritdoc />
    public partial class TheFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seeded only into a board that is still exactly the one the
            // earlier migrations shipped - the five columns, in their order,
            // with their terminal flags. Anything else and this does nothing at
            // all: no rename, no insert, no repoint.
            //
            // That is a harder guard than the one `Playbooks` used, and
            // deliberately. `Playbooks` could measure where `review` went,
            // because one column relative to the first terminal one is a
            // question a board can answer. A whole flow is not: `Breakdown`
            // means nothing except "between Draft and Backlog", and there is no
            // measurement that finds those two on a board somebody arranged by
            // hand. Placing them by guess is how a seeded column lands in the
            // middle of an operator's flow, and a board whose columns no longer
            // alternate is worse than a board that was left alone - the loop
            // would run, and it would run through the gate that was supposed to
            // stop it.
            //
            // So: an exact match, or nothing. The operator of an arranged board
            // has already answered this question, and their answer stands.
            migrationBuilder.Sql("""
            DO $flow$
            BEGIN
                IF (SELECT array_agg("Name" || CASE WHEN "IsTerminal" THEN ' (terminal)' ELSE '' END
                                     ORDER BY "SortOrder", "Id")
                    FROM hatch."Statuses")
                   IS DISTINCT FROM
                   ARRAY['inbox', 'todo', 'in progress', 'review', 'done (terminal)']
                THEN
                    RETURN;
                END IF;

                -- The five that exist keep their row, and so keep their id, the
                -- cards standing in them and the colour the operator gave them.
                -- Only `inbox` changes meaning: it becomes the operator's Draft
                -- rather than the agent's first column, which is the safe
                -- direction for a card to move in - a note nobody has looked at
                -- yet stops being something a loop would pick up.
                UPDATE hatch."Statuses" SET "Name" = 'Draft',       "SortOrder" = 10 WHERE "Name" = 'inbox';
                UPDATE hatch."Statuses" SET "Name" = 'To Do',       "SortOrder" = 40 WHERE "Name" = 'todo';
                UPDATE hatch."Statuses" SET "Name" = 'In Progress', "SortOrder" = 50 WHERE "Name" = 'in progress';
                UPDATE hatch."Statuses" SET "Name" = 'In Review',   "SortOrder" = 60 WHERE "Name" = 'review';
                UPDATE hatch."Statuses" SET "Name" = 'Done',        "SortOrder" = 70 WHERE "Name" = 'done';

                -- The two that make the flow alternate. `Backlog` is the gate
                -- this board did not have: specified work waiting on a person
                -- to choose it, which is the one decision an unattended loop
                -- must not make for itself. `Breakdown` is what Draft used to
                -- be, moved one column right of it so that writing an idea down
                -- and having it specified are different acts.
                --
                -- Both take the neutral grey, as `review` did, because a colour
                -- is the operator's to pick from the Statuses page and a
                -- migration guessing at one is a guess nobody asked for.
                INSERT INTO hatch."Statuses" ("Name", "SortOrder", "IsTerminal", "Color")
                VALUES ('Breakdown', 20, false, '#6b7280'),
                       ('Backlog',   30, false, '#6b7280');

                -- Specifying a draft is a job that moved: it used to run
                -- inbox -> todo and it now runs Breakdown -> Backlog. The rows
                -- move with it rather than being written again, so an operator
                -- who has already retuned a prompt, a model or an effort keeps
                -- every word of it - this only says which transition their
                -- playbook is the playbook for.
                --
                -- Rows that are not there are not restored. Deleting a playbook
                -- is how an operator takes a column back from the loop, and a
                -- migration that re-seeded one would be handing it back.
                UPDATE hatch."Playbooks" p
                   SET "FromStatusId" = breakdown."Id",
                       "ToStatusId"   = backlog."Id"
                  FROM hatch."Statuses" breakdown,
                       hatch."Statuses" backlog,
                       hatch."Statuses" draft,
                       hatch."Statuses" todo
                 WHERE breakdown."Name" = 'Breakdown'
                   AND backlog."Name"   = 'Backlog'
                   AND draft."Name"     = 'Draft'
                   AND todo."Name"      = 'To Do'
                   AND p."FromStatusId" = draft."Id"
                   AND p."ToStatusId"   = todo."Id";
            END
            $flow$;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The mirror of the guard above: only a board this migration is
            // still the last word on is put back. If the operator has since
            // renamed, reordered or added to the flow, rolling this back would
            // be a second unasked-for rearrangement, and one card standing in
            // Breakdown or Backlog is enough to say the board has been used.
            migrationBuilder.Sql("""
            DO $flow$
            BEGIN
                IF (SELECT array_agg("Name" || CASE WHEN "IsTerminal" THEN ' (terminal)' ELSE '' END
                                     ORDER BY "SortOrder", "Id")
                    FROM hatch."Statuses")
                   IS DISTINCT FROM
                   ARRAY['Draft', 'Breakdown', 'Backlog', 'To Do', 'In Progress', 'In Review', 'Done (terminal)']
                THEN
                    RETURN;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM hatch."Issues" i
                    JOIN hatch."Statuses" s ON s."Id" = i."StatusId"
                    WHERE s."Name" IN ('Breakdown', 'Backlog'))
                THEN
                    RETURN;
                END IF;

                UPDATE hatch."Playbooks" p
                   SET "FromStatusId" = draft."Id",
                       "ToStatusId"   = todo."Id"
                  FROM hatch."Statuses" breakdown,
                       hatch."Statuses" backlog,
                       hatch."Statuses" draft,
                       hatch."Statuses" todo
                 WHERE breakdown."Name" = 'Breakdown'
                   AND backlog."Name"   = 'Backlog'
                   AND draft."Name"     = 'Draft'
                   AND todo."Name"      = 'To Do'
                   AND p."FromStatusId" = breakdown."Id"
                   AND p."ToStatusId"   = backlog."Id";

                DELETE FROM hatch."Statuses" WHERE "Name" IN ('Breakdown', 'Backlog');

                UPDATE hatch."Statuses" SET "Name" = 'inbox',       "SortOrder" = 10 WHERE "Name" = 'Draft';
                UPDATE hatch."Statuses" SET "Name" = 'todo',        "SortOrder" = 20 WHERE "Name" = 'To Do';
                UPDATE hatch."Statuses" SET "Name" = 'in progress', "SortOrder" = 30 WHERE "Name" = 'In Progress';
                UPDATE hatch."Statuses" SET "Name" = 'review',      "SortOrder" = 35 WHERE "Name" = 'In Review';
                UPDATE hatch."Statuses" SET "Name" = 'done',        "SortOrder" = 40 WHERE "Name" = 'Done';
            END
            $flow$;
            """);
        }
    }
}
