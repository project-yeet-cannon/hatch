using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOutdoorHazards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AirQualitySamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsAqi = table.Column<int>(type: "integer", nullable: false),
                    Pm25 = table.Column<decimal>(type: "numeric", nullable: true),
                    Pm10 = table.Column<decimal>(type: "numeric", nullable: true),
                    Ozone = table.Column<decimal>(type: "numeric", nullable: true),
                    No2 = table.Column<decimal>(type: "numeric", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AirQualitySamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeatherAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    ProviderAlertId = table.Column<string>(type: "text", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: false),
                    Headline = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Instruction = table.Column<string>(type: "text", nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Onset = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Ends = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AreaDescription = table.Column<string>(type: "text", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AirQualitySamples_Source_Timestamp",
                table: "AirQualitySamples",
                columns: new[] { "Source", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherAlerts_Source_ProviderAlertId",
                table: "WeatherAlerts",
                columns: new[] { "Source", "ProviderAlertId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AirQualitySamples");

            migrationBuilder.DropTable(
                name: "WeatherAlerts");
        }
    }
}
