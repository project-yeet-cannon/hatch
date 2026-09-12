using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hatch.Api.Modules.Hatch.Migrations
{
    /// <summary>
    /// Two columns that turn a comment thread into somewhere a decision can be
    /// asked for and given: a kind, and a link from an answer to its question.
    ///
    /// Both are additive and both have an answer for every row already in the
    /// table - a comment written before today is a note, and notes answer
    /// nothing - so this migration reads the same on an empty database and on
    /// one with a year of threads in it.
    /// </summary>
    public partial class CommentQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AnswersId",
                schema: "hatch",
                table: "Comments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "hatch",
                table: "Comments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AnswersId",
                schema: "hatch",
                table: "Comments",
                column: "AnswersId");

            // No ON DELETE clause, so NO ACTION - deliberately not the RESTRICT
            // the rest of the schema uses. Deleting an issue cascades to every
            // comment on it at once, and PostgreSQL checks a RESTRICT row by
            // row: the answer would be caught pointing at a question that is on
            // its way out and the delete would fail. NO ACTION is checked when
            // the statement finishes, by which point both rows have gone.
            migrationBuilder.AddForeignKey(
                name: "FK_Comments_Comments_AnswersId",
                schema: "hatch",
                table: "Comments",
                column: "AnswersId",
                principalSchema: "hatch",
                principalTable: "Comments",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Comments_Comments_AnswersId",
                schema: "hatch",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_Comments_AnswersId",
                schema: "hatch",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "AnswersId",
                schema: "hatch",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "hatch",
                table: "Comments");
        }
    }
}
