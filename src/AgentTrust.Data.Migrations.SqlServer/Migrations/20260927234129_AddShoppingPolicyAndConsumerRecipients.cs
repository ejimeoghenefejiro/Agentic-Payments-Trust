using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddShoppingPolicyAndConsumerRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipientId",
                table: "ConsumerPurchaseTasks",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConsumerRecipients",
                columns: table => new
                {
                    RecipientId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Relationship = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeliveryConsent = table.Column<bool>(type: "bit", nullable: false),
                    AllowedCategoriesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExcludedCategoriesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreferredMerchantsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExcludedMerchantsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowSubstitutions = table.Column<bool>(type: "bit", nullable: false),
                    NotifyRecipient = table.Column<bool>(type: "bit", nullable: false),
                    PayerNotification = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerRecipients", x => x.RecipientId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPurchaseTasks_RecipientId",
                table: "ConsumerPurchaseTasks",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerRecipients_PrincipalId_Active",
                table: "ConsumerRecipients",
                columns: new[] { "PrincipalId", "Active" });

            migrationBuilder.AddForeignKey(
                name: "FK_ConsumerPurchaseTasks_ConsumerRecipients_RecipientId",
                table: "ConsumerPurchaseTasks",
                column: "RecipientId",
                principalTable: "ConsumerRecipients",
                principalColumn: "RecipientId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConsumerPurchaseTasks_ConsumerRecipients_RecipientId",
                table: "ConsumerPurchaseTasks");

            migrationBuilder.DropTable(
                name: "ConsumerRecipients");

            migrationBuilder.DropIndex(
                name: "IX_ConsumerPurchaseTasks_RecipientId",
                table: "ConsumerPurchaseTasks");

            migrationBuilder.DropColumn(
                name: "RecipientId",
                table: "ConsumerPurchaseTasks");
        }
    }
}
