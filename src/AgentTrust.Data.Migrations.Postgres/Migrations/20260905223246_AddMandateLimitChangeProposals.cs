using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMandateLimitChangeProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MandateLimitChangeProposals",
                columns: table => new
                {
                    ProposalId = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    BaseMandateVersion = table.Column<int>(type: "integer", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    PerTransactionLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    WeeklyLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    MonthlyLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedThrough = table.Column<string>(type: "text", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AppliedBy = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MandateLimitChangeProposals", x => x.ProposalId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MandateLimitChangeProposals_MandateId_Status",
                table: "MandateLimitChangeProposals",
                columns: new[] { "MandateId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MandateLimitChangeProposals_PrincipalId_Status_ExpiresAt",
                table: "MandateLimitChangeProposals",
                columns: new[] { "PrincipalId", "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MandateLimitChangeProposals");
        }
    }
}
