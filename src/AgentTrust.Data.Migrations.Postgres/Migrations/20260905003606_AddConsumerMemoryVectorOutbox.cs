using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumerMemoryVectorOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumerMemoryOutbox",
                columns: table => new
                {
                    OutboxId = table.Column<string>(type: "text", nullable: false),
                    MemoryId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerMemoryOutbox", x => x.OutboxId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerMemoryOutbox_MemoryId_Status",
                table: "ConsumerMemoryOutbox",
                columns: new[] { "MemoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerMemoryOutbox_Status_NextAttemptAt_CreatedAt",
                table: "ConsumerMemoryOutbox",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumerMemoryOutbox");
        }
    }
}
