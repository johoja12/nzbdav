using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCacheStateTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Note: MissingArticleErrors and TimeoutErrors were already added in
            // migration 20260122160707_AddProviderErrorTypesToNzbProviderStats

            migrationBuilder.AddColumn<int>(
                name: "CachePercentage",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CachedBytes",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CachedInInstance",
                table: "DavItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCached",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCacheCheck",
                table: "DavItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Note: MissingArticleErrors and TimeoutErrors are handled by
            // migration 20260122160707_AddProviderErrorTypesToNzbProviderStats

            migrationBuilder.DropColumn(
                name: "CachePercentage",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "CachedBytes",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "CachedInInstance",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "IsCached",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "LastCacheCheck",
                table: "DavItems");
        }
    }
}
