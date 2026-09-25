using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddImmutableCommerceOodaSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommerceOodaSteps",
                columns: table => new
                {
                    StepId = table.Column<string>(type: "text", nullable: false),
                    CycleId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    CycleNumber = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    OutputJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    DecisionReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOodaSteps", x => x.StepId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOodaSteps_CycleId_CycleNumber_Sequence",
                table: "CommerceOodaSteps",
                columns: new[] { "CycleId", "CycleNumber", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOodaSteps_PrincipalId_CreatedAt",
                table: "CommerceOodaSteps",
                columns: new[] { "PrincipalId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceOodaSteps");
        }
    }
}
