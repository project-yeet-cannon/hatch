using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CameraConnections",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: true),
                    DiscoveredHost = table.Column<string>(type: "text", nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    StreamPath = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraConnections", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_CameraConnections_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Label every secret already in the database with the scheme that
            // produced it (docs/plans/cameras.md Phase 11, SecretProtector).
            //
            // This changes no bytes. Scheme v1 *is* the XOR these rows were
            // written with, so the conversion is a three-character prefix, and
            // that is the whole reason the versioning could be introduced
            // without a re-encrypt pass over live credential material.
            //
            // NOT LIKE '%:%' is the guard, and it is exact rather than
            // approximate: a v1 payload is base64, and base64's alphabet has no
            // colon in it, so a row containing one is already tagged. That makes
            // this safe to run twice and safe to run against a database the new
            // code has already written to - which it will have, if the app rolls
            // out before the migration does.
            migrationBuilder.Sql("""
                UPDATE "SiteSettings"
                SET "Value" = 'v1:' || "Value"
                WHERE "Key" IN ('HomeAssistantToken', 'KioskWifiPassword', 'GoogleClientSecret', 'AnthropicApiKey')
                  AND "Value" <> ''
                  AND "Value" NOT LIKE '%:%';
                """);

            migrationBuilder.Sql("""
                UPDATE "CalendarAccounts"
                SET "RefreshToken" = 'v1:' || "RefreshToken"
                WHERE "RefreshToken" <> '' AND "RefreshToken" NOT LIKE '%:%';
                """);

            migrationBuilder.Sql("""
                UPDATE "CalendarAccounts"
                SET "AccessToken" = 'v1:' || "AccessToken"
                WHERE "AccessToken" IS NOT NULL AND "AccessToken" <> '' AND "AccessToken" NOT LIKE '%:%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraConnections");

            // Strip the labels back off, so a rollback leaves rows in the shape
            // the older code reads. Untagged is what it expects, and v1 is its
            // algorithm, so this is lossless in both directions. Scoped to the
            // v1 prefix specifically: a row written under some future scheme
            // must not be stripped down to a payload nothing can decode.
            migrationBuilder.Sql("""
                UPDATE "SiteSettings"
                SET "Value" = substring("Value" from 4)
                WHERE "Key" IN ('HomeAssistantToken', 'KioskWifiPassword', 'GoogleClientSecret', 'AnthropicApiKey')
                  AND "Value" LIKE 'v1:%';
                """);

            migrationBuilder.Sql("""
                UPDATE "CalendarAccounts"
                SET "RefreshToken" = substring("RefreshToken" from 4)
                WHERE "RefreshToken" LIKE 'v1:%';
                """);

            migrationBuilder.Sql("""
                UPDATE "CalendarAccounts"
                SET "AccessToken" = substring("AccessToken" from 4)
                WHERE "AccessToken" LIKE 'v1:%';
                """);
        }
    }
}
