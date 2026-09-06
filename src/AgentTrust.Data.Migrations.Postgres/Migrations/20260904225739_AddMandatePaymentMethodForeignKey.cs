using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMandatePaymentMethodForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "FinancialMandates" WHERE length("PaymentMethodId") > 450) THEN
                        RAISE EXCEPTION 'Cannot add mandate payment-method foreign key: a PaymentMethodId exceeds 450 characters.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "FinancialMandates" m
                        LEFT JOIN "ConsumerPaymentMethods" p ON p."PaymentMethodId" = m."PaymentMethodId"
                        WHERE p."PaymentMethodId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot add mandate payment-method foreign key: an orphaned payment-method reference exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "PaymentMethodId",
                table: "FinancialMandates",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

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
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450);
        }
    }
}
