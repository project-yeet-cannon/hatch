using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerie.Api.Modules.Hatch.Migrations
{
    /// <summary>
    /// A colour per column. Board cards are narrow enough now that the column a
    /// card sits in has to be readable from its edge rather than from its
    /// heading, and the colour is what the drag feedback and the issue page's
    /// status pill are both drawn from.
    /// </summary>
    /// <inheritdoc />
    public partial class StatusColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing column gets the neutral grey, and the four this
            // module seeded get theirs back below. The default is on the column
            // as well as in the entity so that a row written by anything that is
            // not EF still lands with a colour a stylesheet can use.
            migrationBuilder.AddColumn<string>(
                name: "Color",
                schema: "hatch",
                table: "Statuses",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: EfHatchStatus.DefaultColor);

            // Matched by name, which is the only handle there is: the seeded ids
            // were left to the identity column (see the Init migration), and an
            // operator who has since renamed "todo" keeps the grey rather than
            // having this reach for the wrong row. The values are the house
            // chart series from @aerie/ui's tokens - blue for work waiting,
            // amber for work happening, green for work finished.
            Recolor("todo", "#2a78d6");
            Recolor("in progress", "#eda100");
            Recolor("done", "#008300");

            void Recolor(string name, string color) =>
                migrationBuilder.UpdateData(
                    schema: "hatch",
                    table: "Statuses",
                    keyColumn: "Name",
                    keyValue: name,
                    column: "Color",
                    value: color);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                schema: "hatch",
                table: "Statuses");
        }
    }
}
