using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <inheritdoc />
    public partial class IssuePlaybookOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffortOverride",
                schema: "hatch",
                table: "Issues",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelOverride",
                schema: "hatch",
                table: "Issues",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffortOverride",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ModelOverride",
                schema: "hatch",
                table: "Issues");
        }
    }
}
