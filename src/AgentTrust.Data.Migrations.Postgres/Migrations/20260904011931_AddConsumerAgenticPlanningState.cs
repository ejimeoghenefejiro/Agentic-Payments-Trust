using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumerAgenticPlanningState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumerPlanningConversations",
                columns: table => new
                {
                    ConversationId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    Objective = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerPlanningConversations", x => x.ConversationId);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerPlanningTurns",
                columns: table => new
                {
                    TurnId = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "text", nullable: true),
                    ToolInputJson = table.Column<string>(type: "text", nullable: true),
                    ToolOutputJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerPlanningTurns", x => x.TurnId);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerProductReservations",
                columns: table => new
                {
                    ReservationId = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerProductReservations", x => x.ReservationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPlanningConversations_PrincipalId_UpdatedAt",
                table: "ConsumerPlanningConversations",
                columns: new[] { "PrincipalId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPlanningTurns_ConversationId_Sequence",
                table: "ConsumerPlanningTurns",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerProductReservations_ConversationId_ProductId",
                table: "ConsumerProductReservations",
                columns: new[] { "ConversationId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerProductReservations_Status_ExpiresAt",
                table: "ConsumerProductReservations",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumerPlanningConversations");

            migrationBuilder.DropTable(
                name: "ConsumerPlanningTurns");

            migrationBuilder.DropTable(
                name: "ConsumerProductReservations");
        }
    }
}
