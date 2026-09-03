using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableMandatesAndPaymentMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumerPaymentMethods",
                columns: table => new
                {
                    PaymentMethodId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderToken = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CardBrand = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Last4 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiryMonth = table.Column<int>(type: "int", nullable: false),
                    ExpiryYear = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerPaymentMethods", x => x.PaymentMethodId);
                });

            migrationBuilder.CreateTable(
                name: "FinancialMandates",
                columns: table => new
                {
                    MandateId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Merchant = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PaymentMethodId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PerTransactionLimit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DailyLimit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    WeeklyLimit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MonthlyLimit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TaskParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AboveLimit = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SupersedesMandateId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialMandates", x => new { x.MandateId, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPaymentMethods_PrincipalId_Status",
                table: "ConsumerPaymentMethods",
                columns: new[] { "PrincipalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPaymentMethods_Provider_ProviderToken",
                table: "ConsumerPaymentMethods",
                columns: new[] { "Provider", "ProviderToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialMandates_AgentId_Status",
                table: "FinancialMandates",
                columns: new[] { "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialMandates_PrincipalId_Status",
                table: "FinancialMandates",
                columns: new[] { "PrincipalId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumerPaymentMethods");

            migrationBuilder.DropTable(
                name: "FinancialMandates");
        }
    }
}
