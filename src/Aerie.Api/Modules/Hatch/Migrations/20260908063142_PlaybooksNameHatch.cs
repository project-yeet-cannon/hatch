using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <summary>
    /// The seeded playbooks stop telling an agent to run <c>./scripts/hatch.sh</c>.
    /// </summary>
    /// <remarks>
    /// <para>Since AERIE-934 the CLI is one installed program called
    /// <c>hatch</c>, published for four platforms and run from wherever somebody
    /// happens to be standing. <c>./scripts/hatch.sh</c> is a path that only
    /// resolves inside this repository, so a playbook naming it is an
    /// instruction that is false in every other checkout - a friend's install
    /// would be telling its agents to run a file they do not have.</para>
    ///
    /// <para>The other half of the same rename is in the seed migration itself
    /// (<c>20260903204217_Playbooks</c>), which is what a fresh install gets.
    /// EF never replays an applied migration, so editing that file changes
    /// nothing for a database that has already run it - which is exactly what
    /// this one is for.</para>
    ///
    /// <para>A rewrite of the prose rather than a reseed, because a playbook is
    /// a row a person may have edited since and this is a rename, not a
    /// reissue. A prompt that does not name the old path is left alone.</para>
    /// </remarks>
    public partial class PlaybooksNameHatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The trailing space is deliberate. It matches the invocations -
            // "./scripts/hatch.sh pr", "./scripts/hatch.sh ask" - and leaves a
            // bare mention of the file, if anybody wrote one, as the sentence
            // about a file that it is.
            migrationBuilder.Sql(
                """
                UPDATE hatch."Playbooks"
                SET "Prompt" = REPLACE("Prompt", './scripts/hatch.sh ', 'hatch ')
                WHERE "Prompt" LIKE '%./scripts/hatch.sh %';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately nothing.
            //
            // The forward direction is lossy: after it, a prompt contains the
            // word "hatch" in dozens of places and nothing records which of them
            // used to be a path. Replacing every "hatch " with
            // "./scripts/hatch.sh " would corrupt every sentence that merely
            // mentions the tracker, which is most of them - so a down that
            // leaves the prose alone is the only honest one. Reverting the code
            // does not require reverting these words: `hatch` is a name the old
            // wrapper answers to as well.
        }
    }
}
