using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    public partial class CueSheet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StartPositionTicks",
                table: "BaseItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "StartPositionTicks",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartPositionTicks",
                table: "BaseItems");
        }
    }
}
