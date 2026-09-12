using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hatch.Api.Modules.Hatch.Migrations
{
    /// <inheritdoc />
    public partial class IssueAssignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssigneeApiKeyId",
                schema: "hatch",
                table: "Issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssigneePersonId",
                schema: "hatch",
                table: "Issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Issues_OneAssignee",
                schema: "hatch",
                table: "Issues",
                sql: "num_nonnulls(\"AssigneePersonId\", \"AssigneeApiKeyId\") <= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Issues_OneAssignee",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "AssigneeApiKeyId",
                schema: "hatch",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "AssigneePersonId",
                schema: "hatch",
                table: "Issues");
        }
    }
}
