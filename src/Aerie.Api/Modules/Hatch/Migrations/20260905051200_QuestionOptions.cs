using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <summary>
    /// The answers a question offers, as one nullable jsonb column.
    ///
    /// Nullable rather than defaulted to an empty array, because "asked in
    /// prose" and "offered a menu with nothing on it" are different things and
    /// only the first one has ever happened. Every question asked before today
    /// keeps reading exactly as it did.
    /// </summary>
    public partial class QuestionOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Options",
                schema: "hatch",
                table: "Comments",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Options",
                schema: "hatch",
                table: "Comments");
        }
    }
}
