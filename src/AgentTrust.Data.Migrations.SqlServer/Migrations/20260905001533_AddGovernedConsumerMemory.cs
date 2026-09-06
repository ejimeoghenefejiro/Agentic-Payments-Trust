using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernedConsumerMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumerMemories",
                columns: table => new
                {
                    MemoryId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Polarity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceConversationId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourcePurchaseIntentId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Deleted = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerMemories", x => x.MemoryId);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerMemoryRetrievalAudits",
                columns: table => new
                {
                    AuditId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Query = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReturnedMemoryIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerMemoryRetrievalAudits", x => x.AuditId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerMemories_PrincipalId_Deleted_ExpiresAt",
                table: "ConsumerMemories",
                columns: new[] { "PrincipalId", "Deleted", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerMemories_PrincipalId_Kind_Subject",
                table: "ConsumerMemories",
                columns: new[] { "PrincipalId", "Kind", "Subject" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerMemoryRetrievalAudits_PrincipalId_RetrievedAt",
                table: "ConsumerMemoryRetrievalAudits",
                columns: new[] { "PrincipalId", "RetrievedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumerMemories");

            migrationBuilder.DropTable(
                name: "ConsumerMemoryRetrievalAudits");
        }
    }
}
