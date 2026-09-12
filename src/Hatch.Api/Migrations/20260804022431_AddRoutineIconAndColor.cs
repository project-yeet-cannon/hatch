using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hatch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutineIconAndColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Routines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "Routines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                table: "Routines");

            migrationBuilder.DropColumn(
                name: "Icon",
                table: "Routines");
        }
    }
}
