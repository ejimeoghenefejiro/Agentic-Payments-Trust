using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableCommerceOodaCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommerceOodaCycles",
                columns: table => new
                {
                    CycleId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TaskId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CycleNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GoalJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ObservationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlternativesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DecisionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProofJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOodaCycles", x => x.CycleId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOodaCycles_PrincipalId_UpdatedAt",
                table: "CommerceOodaCycles",
                columns: new[] { "PrincipalId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOodaCycles_PurchaseIntentId",
                table: "CommerceOodaCycles",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOodaCycles_Status_UpdatedAt",
                table: "CommerceOodaCycles",
                columns: new[] { "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceOodaCycles");
        }
    }
}
