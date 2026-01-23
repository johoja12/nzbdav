using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRcloneShardRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShardEnabled",
                table: "RcloneInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ShardIndex",
                table: "RcloneInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShardPrefixes",
                table: "RcloneInstances",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsShardEnabled",
                table: "RcloneInstances");

            migrationBuilder.DropColumn(
                name: "ShardIndex",
                table: "RcloneInstances");

            migrationBuilder.DropColumn(
                name: "ShardPrefixes",
                table: "RcloneInstances");
        }
    }
}
