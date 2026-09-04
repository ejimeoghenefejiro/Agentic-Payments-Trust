using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumerConversationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumerConversationPolicies",
                columns: table => new
                {
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    InteractionMode = table.Column<string>(type: "text", nullable: false),
                    AskBeforeSubstitutions = table.Column<bool>(type: "boolean", nullable: false),
                    ShowBasketBeforePayment = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerConversationPolicies", x => x.PrincipalId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumerConversationPolicies");
        }
    }
}
