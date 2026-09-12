using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hatch.Api.Modules.Hatch.Migrations
{
    /// <inheritdoc />
    public partial class IssueDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAt",
                schema: "hatch",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DueAtHasTime",
                schema: "hatch",
                table: "Issues",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadyAt",
                schema: "hatch",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadyAtHasTime",
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
                name: "DueAt",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "DueAtHasTime",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ReadyAt",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ReadyAtHasTime",
                schema: "hatch",
                table: "Issues");
        }
    }
}
