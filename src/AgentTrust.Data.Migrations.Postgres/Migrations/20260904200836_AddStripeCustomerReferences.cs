using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeCustomerReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderCustomerReference",
                table: "ConsumerPaymentMethods",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderCustomerReference",
                table: "ConsumerPaymentMethods");
        }
    }
}
