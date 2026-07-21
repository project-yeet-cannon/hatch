using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsideZone : Migration
    {
        private static readonly Guid OutsideZoneId = new("00000000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Zones",
                columns: new[] { "Id", "Name", "Kind", "ComfortLowF", "ComfortHighF", "SortOrder", "Included" },
                values: new object[] { OutsideZoneId, "Outside", /* ZoneKind.Outside */ 1, null, null, 0, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Zones",
                keyColumn: "Id",
                keyValue: OutsideZoneId);
        }
    }
}
