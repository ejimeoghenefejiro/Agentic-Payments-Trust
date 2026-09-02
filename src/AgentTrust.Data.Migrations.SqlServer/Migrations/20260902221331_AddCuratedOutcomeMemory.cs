using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCuratedOutcomeMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DecisionFeedback",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    InvestigationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AiRecommendation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AgentConfidence = table.Column<double>(type: "float", nullable: true),
                    ActualOutcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HumanConfidence = table.Column<double>(type: "float", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReasonCodesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UsefulEvidenceIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MisleadingEvidenceIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ValidationStatus = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ValidatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionFeedback", x => x.TransactionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionFeedback_InvestigationId",
                table: "DecisionFeedback",
                column: "InvestigationId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionFeedback_ValidationStatus",
                table: "DecisionFeedback",
                column: "ValidationStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DecisionFeedback");
        }
    }
}
