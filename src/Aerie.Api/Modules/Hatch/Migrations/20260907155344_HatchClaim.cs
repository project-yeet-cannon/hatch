using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <inheritdoc />
    public partial class HatchClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimChatter",
                schema: "hatch",
                table: "Issues",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimChatterAt",
                schema: "hatch",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimHeartbeatAt",
                schema: "hatch",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimRunner",
                schema: "hatch",
                table: "Issues",
                type: "character varying(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "hatch",
                table: "Issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                schema: "hatch",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                schema: "hatch",
                table: "Issues",
                type: "character varying(240)",
                maxLength: 240,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimChatter",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ClaimChatterAt",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ClaimHeartbeatAt",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ClaimRunner",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                schema: "hatch",
                table: "Issues");
        }
    }
}
