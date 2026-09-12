using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hatch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPanels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Panels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Icon = table.Column<string>(type: "text", nullable: true),
                    Color = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Included = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Panels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PanelItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PanelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RoutineId = table.Column<Guid>(type: "uuid", nullable: true),
                    ControlKind = table.Column<int>(type: "integer", nullable: true),
                    Label = table.Column<string>(type: "text", nullable: true),
                    Icon = table.Column<string>(type: "text", nullable: true),
                    Color = table.Column<string>(type: "text", nullable: true),
                    OnMode = table.Column<string>(type: "text", nullable: true),
                    MinF = table.Column<decimal>(type: "numeric", nullable: true),
                    MaxF = table.Column<decimal>(type: "numeric", nullable: true),
                    StepF = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PanelItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PanelItems_Panels_PanelId",
                        column: x => x.PanelId,
                        principalTable: "Panels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PanelItems_Routines_RoutineId",
                        column: x => x.RoutineId,
                        principalTable: "Routines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PanelControlBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PanelControlBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PanelControlBindings_DeviceChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "DeviceChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PanelControlBindings_PanelItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "PanelItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PanelControlBindings_ChannelId",
                table: "PanelControlBindings",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_PanelControlBindings_ItemId",
                table: "PanelControlBindings",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PanelItems_PanelId",
                table: "PanelItems",
                column: "PanelId");

            migrationBuilder.CreateIndex(
                name: "IX_PanelItems_RoutineId",
                table: "PanelItems",
                column: "RoutineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PanelControlBindings");

            migrationBuilder.DropTable(
                name: "PanelItems");

            migrationBuilder.DropTable(
                name: "Panels");
        }
    }
}
