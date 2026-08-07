using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ControlDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: false),
                    ChosenActionJson = table.Column<string>(type: "text", nullable: false),
                    RunnerUpJson = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    Score = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedValue = table.Column<string>(type: "text", nullable: true),
                    OverriddenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Commands_ControlDecisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "ControlDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Commands_DeviceChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "DeviceChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ControlOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpectedState = table.Column<string>(type: "text", nullable: true),
                    ObservedState = table.Column<string>(type: "text", nullable: true),
                    SuppressedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControlOverrides_Commands_CommandId",
                        column: x => x.CommandId,
                        principalTable: "Commands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ControlOverrides_DeviceChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "DeviceChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ControlOverrides_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_ChannelId_RequestedAt",
                table: "Commands",
                columns: new[] { "ChannelId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_DecisionId",
                table: "Commands",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ControlDecisions_Timestamp",
                table: "ControlDecisions",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ControlOverrides_ChannelId",
                table: "ControlOverrides",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ControlOverrides_CommandId",
                table: "ControlOverrides",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_ControlOverrides_DeviceId_SuppressedUntil",
                table: "ControlOverrides",
                columns: new[] { "DeviceId", "SuppressedUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlOverrides");

            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "ControlDecisions");
        }
    }
}
