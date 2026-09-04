using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
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
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    InteractionMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AskBeforeSubstitutions = table.Column<bool>(type: "bit", nullable: false),
                    ShowBasketBeforePayment = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
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
