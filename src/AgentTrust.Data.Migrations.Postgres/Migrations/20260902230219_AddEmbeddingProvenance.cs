using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmbeddingCreatedAt",
                table: "SemanticCases",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimensions",
                table: "SemanticCases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "SemanticCases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModelVersion",
                table: "SemanticCases",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingProvider",
                table: "SemanticCases",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingCreatedAt",
                table: "SemanticCases");

            migrationBuilder.DropColumn(
                name: "EmbeddingDimensions",
                table: "SemanticCases");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "SemanticCases");

            migrationBuilder.DropColumn(
                name: "EmbeddingModelVersion",
                table: "SemanticCases");

            migrationBuilder.DropColumn(
                name: "EmbeddingProvider",
                table: "SemanticCases");
        }
    }
}
