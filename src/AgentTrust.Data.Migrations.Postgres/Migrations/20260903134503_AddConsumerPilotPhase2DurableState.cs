using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumerPilotPhase2DurableState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CheckoutExecutions",
                columns: table => new
                {
                    CheckoutExecutionId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    PaymentIdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SubmissionCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutExecutions", x => x.CheckoutExecutionId);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedServices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ExternalAccountReference = table.Column<string>(type: "text", nullable: false),
                    ConnectionType = table.Column<string>(type: "text", nullable: false),
                    CredentialReference = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedServices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerPaymentAttempts",
                columns: table => new
                {
                    PaymentAttemptId = table.Column<string>(type: "text", nullable: false),
                    CheckoutExecutionId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    PaymentIdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "text", nullable: true),
                    ProviderCustomerId = table.Column<string>(type: "text", nullable: true),
                    ProviderPaymentMethodId = table.Column<string>(type: "text", nullable: false),
                    LatestStatus = table.Column<string>(type: "text", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerPaymentAttempts", x => x.PaymentAttemptId);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerProfiles",
                columns: table => new
                {
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Timezone = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerProfiles", x => x.PrincipalId);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerPurchaseTasks",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    MerchantScopeJson = table.Column<string>(type: "text", nullable: false),
                    Schedule = table.Column<string>(type: "text", nullable: false),
                    Timezone = table.Column<string>(type: "text", nullable: false),
                    MaximumAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    ShoppingListJson = table.Column<string>(type: "text", nullable: false),
                    PreferencesJson = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    PaymentMethodId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    NextExecutionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerPurchaseTasks", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "OneOffAuthorisations",
                columns: table => new
                {
                    AuthorisationId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    TransactionFingerprint = table.Column<string>(type: "text", nullable: false),
                    MaximumAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    PaymentMethodReference = table.Column<string>(type: "text", nullable: false),
                    ApprovedBy = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneOffAuthorisations", x => x.AuthorisationId);
                });

            migrationBuilder.CreateTable(
                name: "PendingConsumerApprovals",
                columns: table => new
                {
                    ApprovalId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    IntentHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ApproverSubject = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingConsumerApprovals", x => x.ApprovalId);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseAuthorisations",
                columns: table => new
                {
                    AuthorisationId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    AuthorisedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    IntentHash = table.Column<string>(type: "text", nullable: false),
                    PolicyVersion = table.Column<string>(type: "text", nullable: false),
                    SigningKeyId = table.Column<string>(type: "text", nullable: false),
                    Algorithm = table.Column<string>(type: "text", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseAuthorisations", x => x.AuthorisationId);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseExecutions",
                columns: table => new
                {
                    ExecutionId = table.Column<string>(type: "text", nullable: false),
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: true),
                    ProviderPaymentId = table.Column<string>(type: "text", nullable: true),
                    RequiredAction = table.Column<string>(type: "text", nullable: true),
                    ReasonsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseExecutions", x => x.ExecutionId);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseIntents",
                columns: table => new
                {
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    ExecutionId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    MerchantName = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    BasketJson = table.Column<string>(type: "text", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DeliveryFee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DeliveryAddressReference = table.Column<string>(type: "text", nullable: false),
                    RequestedDeliveryWindow = table.Column<string>(type: "text", nullable: true),
                    PaymentMethodReference = table.Column<string>(type: "text", nullable: false),
                    IntentHash = table.Column<string>(type: "text", nullable: false),
                    PaymentIdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    QuoteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseIntents", x => x.PurchaseIntentId);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseLifecycleEvents",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: true),
                    IntentHash = table.Column<string>(type: "text", nullable: false),
                    PreviousHash = table.Column<string>(type: "text", nullable: false),
                    CurrentHash = table.Column<string>(type: "text", nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseLifecycleEvents", x => x.SequenceNumber);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReceipts",
                columns: table => new
                {
                    ReceiptId = table.Column<string>(type: "text", nullable: false),
                    PurchaseIntentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "text", nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReceipts", x => x.ReceiptId);
                });

            migrationBuilder.CreateTable(
                name: "SpendReservations",
                columns: table => new
                {
                    ReservationId = table.Column<string>(type: "text", nullable: false),
                    MandateId = table.Column<string>(type: "text", nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    ExecutionId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendReservations", x => x.ReservationId);
                });

            migrationBuilder.CreateTable(
                name: "StripeWebhookEvents",
                columns: table => new
                {
                    ProviderEventId = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "text", nullable: true),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProviderCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeWebhookEvents", x => x.ProviderEventId);
                });

            migrationBuilder.CreateTable(
                name: "TaskOccurrences",
                columns: table => new
                {
                    OccurrenceId = table.Column<string>(type: "text", nullable: false),
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ClaimedBy = table.Column<string>(type: "text", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskOccurrences", x => x.OccurrenceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutExecutions_PaymentIdempotencyKey",
                table: "CheckoutExecutions",
                column: "PaymentIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutExecutions_PurchaseIntentId",
                table: "CheckoutExecutions",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedServices_PrincipalId_Provider_ExternalAccountRefer~",
                table: "ConnectedServices",
                columns: new[] { "PrincipalId", "Provider", "ExternalAccountReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPaymentAttempts_LatestStatus_UpdatedAt",
                table: "ConsumerPaymentAttempts",
                columns: new[] { "LatestStatus", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPaymentAttempts_PaymentIdempotencyKey",
                table: "ConsumerPaymentAttempts",
                column: "PaymentIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPaymentAttempts_ProviderPaymentId",
                table: "ConsumerPaymentAttempts",
                column: "ProviderPaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPurchaseTasks_NextExecutionAt",
                table: "ConsumerPurchaseTasks",
                column: "NextExecutionAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerPurchaseTasks_PrincipalId_Status",
                table: "ConsumerPurchaseTasks",
                columns: new[] { "PrincipalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OneOffAuthorisations_PurchaseIntentId",
                table: "OneOffAuthorisations",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OneOffAuthorisations_TransactionFingerprint",
                table: "OneOffAuthorisations",
                column: "TransactionFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingConsumerApprovals_PrincipalId_Status",
                table: "PendingConsumerApprovals",
                columns: new[] { "PrincipalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingConsumerApprovals_PurchaseIntentId",
                table: "PendingConsumerApprovals",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseAuthorisations_PurchaseIntentId",
                table: "PurchaseAuthorisations",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseAuthorisations_Status_ExpiresAt",
                table: "PurchaseAuthorisations",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseExecutions_PrincipalId_State",
                table: "PurchaseExecutions",
                columns: new[] { "PrincipalId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseExecutions_ProviderPaymentId",
                table: "PurchaseExecutions",
                column: "ProviderPaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseExecutions_PurchaseIntentId",
                table: "PurchaseExecutions",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseExecutions_TaskId_ScheduledFor",
                table: "PurchaseExecutions",
                columns: new[] { "TaskId", "ScheduledFor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseIntents_ExecutionId",
                table: "PurchaseIntents",
                column: "ExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseIntents_PaymentIdempotencyKey",
                table: "PurchaseIntents",
                column: "PaymentIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseIntents_PrincipalId_CreatedAt",
                table: "PurchaseIntents",
                columns: new[] { "PrincipalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseLifecycleEvents_EventId",
                table: "PurchaseLifecycleEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseLifecycleEvents_PurchaseIntentId_SequenceNumber",
                table: "PurchaseLifecycleEvents",
                columns: new[] { "PurchaseIntentId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_PrincipalId_PurchasedAt",
                table: "PurchaseReceipts",
                columns: new[] { "PrincipalId", "PurchasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_ProviderPaymentId",
                table: "PurchaseReceipts",
                column: "ProviderPaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_PurchaseIntentId",
                table: "PurchaseReceipts",
                column: "PurchaseIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpendReservations_ExecutionId",
                table: "SpendReservations",
                column: "ExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpendReservations_MandateId_Status_ReservedAt",
                table: "SpendReservations",
                columns: new[] { "MandateId", "Status", "ReservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StripeWebhookEvents_Status_ReceivedAt",
                table: "StripeWebhookEvents",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_Status_LeaseExpiresAt",
                table: "TaskOccurrences",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_TaskId_ScheduledFor",
                table: "TaskOccurrences",
                columns: new[] { "TaskId", "ScheduledFor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckoutExecutions");

            migrationBuilder.DropTable(
                name: "ConnectedServices");

            migrationBuilder.DropTable(
                name: "ConsumerPaymentAttempts");

            migrationBuilder.DropTable(
                name: "ConsumerProfiles");

            migrationBuilder.DropTable(
                name: "ConsumerPurchaseTasks");

            migrationBuilder.DropTable(
                name: "OneOffAuthorisations");

            migrationBuilder.DropTable(
                name: "PendingConsumerApprovals");

            migrationBuilder.DropTable(
                name: "PurchaseAuthorisations");

            migrationBuilder.DropTable(
                name: "PurchaseExecutions");

            migrationBuilder.DropTable(
                name: "PurchaseIntents");

            migrationBuilder.DropTable(
                name: "PurchaseLifecycleEvents");

            migrationBuilder.DropTable(
                name: "PurchaseReceipts");

            migrationBuilder.DropTable(
                name: "SpendReservations");

            migrationBuilder.DropTable(
                name: "StripeWebhookEvents");

            migrationBuilder.DropTable(
                name: "TaskOccurrences");
        }
    }
}
