using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hatch.Api.Modules.Hatch.Migrations
{
    /// <inheritdoc />
    public partial class Playbooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playbooks",
                schema: "hatch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromStatusId = table.Column<int>(type: "integer", nullable: false),
                    ToStatusId = table.Column<int>(type: "integer", nullable: false),
                    Types = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Model = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Effort = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playbooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Playbooks_Statuses_FromStatusId",
                        column: x => x.FromStatusId,
                        principalSchema: "hatch",
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Playbooks_Statuses_ToStatusId",
                        column: x => x.ToStatusId,
                        principalSchema: "hatch",
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Playbooks_FromStatusId_ToStatusId_Types",
                schema: "hatch",
                table: "Playbooks",
                columns: new[] { "FromStatusId", "ToStatusId", "Types" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Playbooks_ToStatusId",
                schema: "hatch",
                table: "Playbooks",
                column: "ToStatusId");

            // A column between "in progress" and "done", because the flow now has
            // an end an agent may reach and an end only the operator may. Work
            // is pushed into review by whoever did it; moving review into done
            // is a judgement about whether it shipped, and that judgement is
            // not delegated (CLAUDE.md, "Implementing a ticket").
            //
            // Placed by measurement rather than at a hardcoded 35: an install
            // that has already renamed or reordered its columns gets the review
            // column immediately left of its first terminal one, and an install
            // with no terminal column at all gets it on the right. Skipped
            // where a "review" already exists, so an operator who added one by
            // hand does not end up with two.
            migrationBuilder.Sql("""
            INSERT INTO hatch."Statuses" ("Name", "SortOrder", "IsTerminal", "Color")
            SELECT 'review',
                   COALESCE(
                     (SELECT MIN("SortOrder") - 5 FROM hatch."Statuses" WHERE "IsTerminal"),
                     (SELECT MAX("SortOrder") + 10 FROM hatch."Statuses"),
                     10),
                   false,
                   '#6b7280'
            WHERE NOT EXISTS (SELECT 1 FROM hatch."Statuses" WHERE "Name" = 'review');
            """);

            // The matrix every install starts with, seeded here for the reason
            // the columns are (see Init): a Hatch whose agent loop cannot run
            // until somebody has filled in a table is a Hatch that ships
            // broken, and a first-run seeder elsewhere would be a second place
            // deciding what a fresh install looks like. Nothing re-asserts
            // these afterwards - the operator retunes them from the Playbooks
            // page, and those edits are theirs to keep.
            //
            // Joined on column name rather than id, because ids are
            // identity-generated and this migration cannot know them. An
            // install that renamed its columns first seeds nothing at all,
            // which is the right failure: a playbook wired to the wrong
            // transition is worse than an empty table.
            //
            // Dollar-quoted because the prompts are prose, and prose has
            // apostrophes in it.
            migrationBuilder.Sql("""
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", 'epic', $prompt$You are turning a captured idea into an epic somebody else could execute.

            What lands in the drafting column is intent, not a specification: a paragraph, a
            grievance, a link. Your increment is to make it real, and to stop there.

            - Read what has already happened to this issue - its comments and its events -
              before you start anything. A previous run may have been aborted partway, and
              continuing it beats writing over it. If it ran to completion and only the
              status was left behind, say so and move it on.
            - Ask the repository whatever the ticket does not answer. The code is the source
              of truth about what already exists; the ticket is only the source of truth
              about what is wanted.
            - Rewrite the description as the epic's own overview and architecture document:
              what this is for, what done looks like, the decisions taken and the ones
              rejected with the reason, and the constraints that bound it.
            - Break it into stories, each with parentKey set to this epic. A story is one
              landable outcome, not a phase of work. If you cannot say what a story delivers
              in one sentence, it is two stories.
            - Sequence them. Anything that has to wait for a date or an event gets a
              readyAt; anything owed gets a dueAt. A note in a description saying "not until
              March" is a note nobody will see in March.

            Write no implementation code in this increment. Breaking work down and doing it
            are different jobs, and done in one session the plan comes out shaped like
            whatever you happened to build first.$prompt$, 'fable', 'max', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'inbox' AND t."Name" = 'todo'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = 'epic');
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", 'story', $prompt$You are turning a captured idea into a story that can be picked up cold.

            - Read the issue's comments and events first. A previous run may have been
              aborted partway; continue it rather than starting again, and if it ran to
              completion and only the status was left behind, say so and move it on.
            - Say who it is for and what they get. The objective has to be concrete enough
              that somebody could disagree with it.
            - Write acceptance criteria as user-observable phenomena: an ordinal outline,
              one behaviour to a line, each an observable fact about the finished system
              rather than a task you intend to perform. "The board folds issues whose ready
              date has not arrived" is a criterion; "add folding logic" is not. Technical
              detail is not an acceptance criterion - it is a child task.
            - Name the files and the pattern to copy, by path, where you found them. Half of
              what makes a story cheap is that the next session does not search twice.
            - Write the implementation plan into the description, and where it is
              nontrivially complex, cut it into tasks with parentKey set to this story. A
              story that is one afternoon does not need three tasks.

            Research the repository as much as the criteria need to be accurate. Write no
            implementation code; that is a later increment's job.$prompt$, 'opus', 'xhigh', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'inbox' AND t."Name" = 'todo'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = 'story');
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", 'task,bug', $prompt$You are formalising a captured note into a task somebody can execute without
            having to ask a question first.

            - Read the issue's comments and events first. A previous run may have been
              aborted partway; continue it rather than starting again, and if it ran to
              completion and only the status was left behind, say so and move it on.
            - Rewrite the description as what is wrong or missing and what the finished
              state is. For a bug: what happens, what should happen, and how to reproduce it
              if you can work that out.
            - Find the code it concerns and name it by path and line.
            - Name the pattern file to copy if the house already does this thing somewhere.
              The house has one way of doing each thing; copying it beats inventing a
              second.
            - Give it a single unified technical acceptance criterion rather than a list of
              them. A task is one thing done; if it needs a list, it is more than one task.

            Write no implementation code in this increment.$prompt$, 'opus', 'high', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'inbox' AND t."Name" = 'todo'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = 'task,bug');
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", 'epic', $prompt$You are starting an epic, which means choosing what starts - not building it.

            - Read the epic, its children, and what has already happened to it. A previous
              run may have been aborted partway; continue it rather than starting again.
            - If it has no stories under it, it reached this column too early. Say so on the
              ticket, leave it where it is, and stop.
            - Choose the story that unblocks the most of the rest, respecting ready dates.
            - Leave that story where it is. The next increment picks it up on its own
              merits, and moving it now would claim work nobody has started.
            - Comment on the epic saying which story is next and why that one.

            Write no implementation code in this increment.$prompt$, 'opus', 'high', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'todo' AND t."Name" = 'in progress'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = 'epic');
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", '', $prompt$You are doing the final analysis of a ticket that has already been specified,
            and leaving it in a state where implementing it is mechanical.

            The next increment writes the code. Everything it would otherwise have to decide
            - what done means, which files change, which pattern to copy, what the tests are
            - is yours to settle now and to write onto the ticket, because a decision taken
            with the code half-written is taken under pressure to make the code already
            written correct.

            - Read the issue's comments and events first. A previous run may have been
              aborted partway; continue it rather than starting again, and if it ran to
              completion and only the status was left behind, say so and move it on.
            - Research the repository until the plan is accurate rather than plausible. What
              the repository can answer, answer by reading the repository.
            - Sharpen the acceptance criteria until each is clear, concise and specific. On
              a story they are user-observable phenomena, an ordinal outline with one
              behaviour to a line; on a task or a bug they are one unified technical
              criterion. Technical detail is not an acceptance criterion.
            - Write the implementation detail onto the ticket: the files that change, by
              path; the pattern file to copy; the shape of the tests; and the order the work
              goes in. Where the plan has real seams, cut it into child tasks carrying that
              detail, so that each is one thing done.
            - Say what you checked and what you could not check. The reasoning belongs on
              the ticket, not in a session log nobody can read afterwards.

            Write no implementation code and make no commit in this increment. Nothing here
            changes the tree.$prompt$, 'opus', 'high', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'todo' AND t."Name" = 'in progress'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = '');
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", 'epic', $prompt$You are deciding whether an epic is finished, not finishing it.

            - Read the epic, its children, and what has already happened to it.
            - If any child is unfinished, the epic is not ready for review. Comment naming
              the ones outstanding and what each is waiting on, leave the epic where it is,
              and stop. A sentence a person can act on is worth more than motion on the
              board.
            - If they are all finished, comment the summary a reviewer needs: what the epic
              delivered, what was cut and why, and anything you are unsure about.

            Write no implementation code in this increment. A child that still needs work is
            a child to work in its own increment.$prompt$, 'sonnet', 'medium', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'in progress' AND t."Name" = 'review'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = 'epic');
            INSERT INTO hatch."Playbooks"
                ("FromStatusId", "ToStatusId", "Types", "Prompt", "Model", "Effort", "CreatedAt", "UpdatedAt")
            SELECT f."Id", t."Id", '', $prompt$You are implementing a ticket that has already been analysed. The description is
            the brief, its acceptance criteria are the definition of done, and its child
            tasks are the plan.

            - Read what has already happened to it - its comments and its events - so that
              you continue the work rather than start it again. A previous run may have been
              aborted partway; if it ran to completion and only the status was left behind,
              say so and move it on.
            - Branch from the remote, not from local main: fetch first, then cut
              <key-lowercased>-<short-slug> from origin/main. Another session may share this
              tree, and a branch cut from a local main carries their unpushed commits into
              your push.
            - Work the child tasks in order, testing and committing along the way as it
              makes sense to. Read the pattern file the ticket names before writing the
              thing it patterns.
            - Build with make (make build, make test-api, make test-web), never a bare
              dotnet - the npm step needs the shell profile. No hardcoded domains,
              addresses, hostnames or people anywhere, including in comments.
            - Green before pushed: lint, build, tests. The operator does all browser and UI
              verification, so never claim a screen works - only that it builds. If
              something is red and you cannot fix it, stop, comment what you found, and do
              not push.
            - Check the acceptance criteria one at a time and say which are met. A criterion
              you cannot verify is one to say out loud you cannot verify, not one to quietly
              count.
            - Commit and push, the subject in house style: an area, then what changed, as a
              sentence. If there is no code change, make no commit - say what you found
              instead.
            - Comment the summary a reviewer needs: the branch, the sha, what changed, what
              to look at first, what did not land, and anything you are unsure about. If you
              opened a pull request, record it on the issue with hatch pr.
            - Move the child tasks you worked along with the issue. A task left standing in
              the column it started in reads as work nobody did.$prompt$, 'sonnet', 'high', now(), now()
            FROM hatch."Statuses" f, hatch."Statuses" t
            WHERE f."Name" = 'in progress' AND t."Name" = 'review'
              AND NOT EXISTS (
                SELECT 1 FROM hatch."Playbooks" p
                WHERE p."FromStatusId" = f."Id" AND p."ToStatusId" = t."Id" AND p."Types" = '');
            """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Playbooks",
                schema: "hatch");

            // Only if it is empty. A rolled-back migration must not take a
            // board's worth of cards with it, and by this point the operator
            // may have been moving work into review for months.
            migrationBuilder.Sql("""
            DELETE FROM hatch."Statuses" s
            WHERE s."Name" = 'review'
              AND NOT EXISTS (SELECT 1 FROM hatch."Issues" i WHERE i."StatusId" = s."Id");
            """);

            // Only if it is empty. A rolled-back migration must not take a
            // board's worth of cards with it, and by this point the operator
            // may have been moving work into review for months.
            migrationBuilder.Sql("""
            DELETE FROM hatch."Statuses" s
            WHERE s."Name" = 'review'
              AND NOT EXISTS (SELECT 1 FROM hatch."Issues" i WHERE i."StatusId" = s."Id");
            """);
        }
    }
}
