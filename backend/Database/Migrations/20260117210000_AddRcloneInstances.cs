using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRcloneInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RcloneInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Password = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RemoteName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableDirRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnablePrefetch = table.Column<bool>(type: "INTEGER", nullable: false),
                    VfsCachePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastTestedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastTestSuccess = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RcloneInstances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RcloneInstances_IsEnabled",
                table: "RcloneInstances",
                column: "IsEnabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RcloneInstances");
        }
    }
}
