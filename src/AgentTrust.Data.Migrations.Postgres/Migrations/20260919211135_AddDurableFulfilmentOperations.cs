using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableFulfilmentOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FulfilmentCancellations",
                columns: table => new
                {
                    CancellationId = table.Column<string>(type: "text", nullable: false),
                    FulfilmentId = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CancellationFee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    ProviderReference = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfilmentCancellations", x => x.CancellationId);
                });

            migrationBuilder.CreateTable(
                name: "FulfilmentExecutions",
                columns: table => new
                {
                    FulfilmentIntentId = table.Column<string>(type: "text", nullable: false),
                    FulfilmentId = table.Column<string>(type: "text", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProviderReference = table.Column<string>(type: "text", nullable: true),
                    CourierReference = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    ReconciliationAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextReconciliationAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfilmentExecutions", x => x.FulfilmentIntentId);
                });

            migrationBuilder.CreateTable(
                name: "FulfilmentIntents",
                columns: table => new
                {
                    FulfilmentIntentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    MerchantOrderId = table.Column<string>(type: "text", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    QuoteId = table.Column<string>(type: "text", nullable: false),
                    QuoteHash = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DestinationReference = table.Column<string>(type: "text", nullable: true),
                    PickupLocationId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfilmentIntents", x => x.FulfilmentIntentId);
                });

            migrationBuilder.CreateTable(
                name: "FulfilmentQuotes",
                columns: table => new
                {
                    QuoteId = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    QuoteHash = table.Column<string>(type: "text", nullable: false),
                    ProviderReference = table.Column<string>(type: "text", nullable: false),
                    QuoteJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfilmentQuotes", x => x.QuoteId);
                });

            migrationBuilder.CreateTable(
                name: "FulfilmentStatusHistory",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderEventId = table.Column<string>(type: "text", nullable: false),
                    FulfilmentId = table.Column<string>(type: "text", nullable: false),
                    PreviousStatus = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProviderTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfilmentStatusHistory", x => x.SequenceNumber);
                });

            migrationBuilder.CreateTable(
                name: "FulfilmentWebhookEvents",
                columns: table => new
                {
                    ProviderEventId = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfilmentWebhookEvents", x => x.ProviderEventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentCancellations_FulfilmentId_Status",
                table: "FulfilmentCancellations",
                columns: new[] { "FulfilmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentCancellations_IdempotencyKey",
                table: "FulfilmentCancellations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentExecutions_IdempotencyKey",
                table: "FulfilmentExecutions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentExecutions_ProviderReference",
                table: "FulfilmentExecutions",
                column: "ProviderReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentExecutions_Status_NextReconciliationAt",
                table: "FulfilmentExecutions",
                columns: new[] { "Status", "NextReconciliationAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentIntents_PrincipalId_CreatedAt",
                table: "FulfilmentIntents",
                columns: new[] { "PrincipalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentIntents_QuoteId",
                table: "FulfilmentIntents",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentQuotes_ProviderId_ExpiresAt",
                table: "FulfilmentQuotes",
                columns: new[] { "ProviderId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentStatusHistory_FulfilmentId_SequenceNumber",
                table: "FulfilmentStatusHistory",
                columns: new[] { "FulfilmentId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentStatusHistory_ProviderEventId",
                table: "FulfilmentStatusHistory",
                column: "ProviderEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfilmentWebhookEvents_ProviderId_Status_ReceivedAt",
                table: "FulfilmentWebhookEvents",
                columns: new[] { "ProviderId", "Status", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FulfilmentCancellations");

            migrationBuilder.DropTable(
                name: "FulfilmentExecutions");

            migrationBuilder.DropTable(
                name: "FulfilmentIntents");

            migrationBuilder.DropTable(
                name: "FulfilmentQuotes");

            migrationBuilder.DropTable(
                name: "FulfilmentStatusHistory");

            migrationBuilder.DropTable(
                name: "FulfilmentWebhookEvents");
        }
    }
}
