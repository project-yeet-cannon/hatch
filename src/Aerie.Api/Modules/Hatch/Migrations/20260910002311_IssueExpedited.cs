using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <inheritdoc />
    public partial class IssueExpedited : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Expedited",
                schema: "hatch",
                table: "Issues",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Expedited",
                schema: "hatch",
                table: "Issues");
        }
    }
}
