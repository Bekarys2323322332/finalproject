using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CvManager.Migrations
{
    /// <inheritdoc />
    public partial class AttributeLastUsed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedAt",
                table: "LibraryAttributes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "LibraryAttributes",
                keyColumn: "Id",
                keyValue: 1,
                column: "LastUsedAt",
                value: null);

            migrationBuilder.UpdateData(
                table: "LibraryAttributes",
                keyColumn: "Id",
                keyValue: 2,
                column: "LastUsedAt",
                value: null);

            migrationBuilder.UpdateData(
                table: "LibraryAttributes",
                keyColumn: "Id",
                keyValue: 3,
                column: "LastUsedAt",
                value: null);

            migrationBuilder.UpdateData(
                table: "LibraryAttributes",
                keyColumn: "Id",
                keyValue: 4,
                column: "LastUsedAt",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "LibraryAttributes");
        }
    }
}
