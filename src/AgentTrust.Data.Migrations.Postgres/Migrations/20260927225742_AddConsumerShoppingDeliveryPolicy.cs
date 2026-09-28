using AgentTrust.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentTrust.Data.Migrations.Postgres.Migrations;

[DbContext(typeof(AgentTrustDbContext))]
[Migration("20260927225742_AddConsumerShoppingDeliveryPolicy")]
public sealed class AddConsumerShoppingDeliveryPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "ConsumerShoppingDeliveryPolicies",
        columns: table => new
        {
            PrincipalId = table.Column<string>(type: "character varying(450)", nullable: false),
            DeliveryAddress = table.Column<string>(type: "text", nullable: true),
            Postcode = table.Column<string>(type: "text", nullable: true),
            PreferredMerchantsJson = table.Column<string>(type: "text", nullable: false),
            ExcludedMerchantsJson = table.Column<string>(type: "text", nullable: false),
            MaximumDistanceMiles = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
            AllowAlternativeMerchants = table.Column<bool>(type: "boolean", nullable: false),
            MaximumAdditionalDeliveryCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
            AllowSplitOrders = table.Column<bool>(type: "boolean", nullable: false),
            AllowCrossBrandSubstitutions = table.Column<bool>(type: "boolean", nullable: false),
            AskBeforeNonPreferredMerchant = table.Column<bool>(type: "boolean", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Version = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ConsumerShoppingDeliveryPolicies", x => x.PrincipalId));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ConsumerShoppingDeliveryPolicies");
}
