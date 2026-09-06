using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMandatePaymentMethodForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [FinancialMandates] WHERE LEN([PaymentMethodId]) > 450)
                    THROW 51000, 'Cannot add mandate payment-method foreign key: a PaymentMethodId exceeds 450 characters.', 1;
                IF EXISTS (
                    SELECT 1 FROM [FinancialMandates] m
                    LEFT JOIN [ConsumerPaymentMethods] p ON p.[PaymentMethodId] = m.[PaymentMethodId]
                    WHERE p.[PaymentMethodId] IS NULL)
                    THROW 51000, 'Cannot add mandate payment-method foreign key: an orphaned payment-method reference exists.', 1;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "PaymentMethodId",
                table: "FinancialMandates",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialMandates_PaymentMethodId",
                table: "FinancialMandates",
                column: "PaymentMethodId");

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialMandates_ConsumerPaymentMethods_PaymentMethodId",
                table: "FinancialMandates",
                column: "PaymentMethodId",
                principalTable: "ConsumerPaymentMethods",
                principalColumn: "PaymentMethodId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FinancialMandates_ConsumerPaymentMethods_PaymentMethodId",
                table: "FinancialMandates");

            migrationBuilder.DropIndex(
                name: "IX_FinancialMandates_PaymentMethodId",
                table: "FinancialMandates");

            migrationBuilder.AlterColumn<string>(
                name: "PaymentMethodId",
                table: "FinancialMandates",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);
        }
    }
}
