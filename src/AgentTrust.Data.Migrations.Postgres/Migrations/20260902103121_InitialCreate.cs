using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentTrust.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    AgentType = table.Column<string>(type: "text", nullable: false),
                    Environment = table.Column<string>(type: "text", nullable: false),
                    CredentialStatus = table.Column<string>(type: "text", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuerTrustAnchor = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.AgentId);
                });

            migrationBuilder.CreateTable(
                name: "Approvals",
                columns: table => new
                {
                    ApprovalId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OriginalDecision = table.Column<string>(type: "text", nullable: false),
                    Approver = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    FinalOutcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Approvals", x => x.ApprovalId);
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    AuthorityId = table.Column<string>(type: "text", nullable: false),
                    PolicyVersion = table.Column<string>(type: "text", nullable: false),
                    RecordJson = table.Column<string>(type: "text", nullable: false),
                    PreviousHash = table.Column<string>(type: "text", nullable: false),
                    CurrentHash = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.SequenceNumber);
                });

            migrationBuilder.CreateTable(
                name: "Authorities",
                columns: table => new
                {
                    AuthorityId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    Permissions = table.Column<string>(type: "text", nullable: false),
                    PerTransactionLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DailyLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ApprovedMerchants = table.Column<string>(type: "text", nullable: false),
                    CategoryScope = table.Column<string>(type: "text", nullable: false),
                    GeographicScope = table.Column<string>(type: "text", nullable: false),
                    WindowStart = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    WindowEnd = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    HumanApprovalAbove = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Authorities", x => x.AuthorityId);
                });

            migrationBuilder.CreateTable(
                name: "Bindings",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    BoundAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    BindingEvidenceRef = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bindings", x => x.AgentId);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceManifests",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    CitedEvidenceJson = table.Column<string>(type: "text", nullable: false),
                    RequiredEvidenceTypesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceManifests", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "Merchants",
                columns: table => new
                {
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchants", x => x.MerchantId);
                });

            migrationBuilder.CreateTable(
                name: "PaymentOutcomes",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProviderReference = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOutcomes", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "PolicyDecisions",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    ChecksJson = table.Column<string>(type: "text", nullable: false),
                    PolicyVersion = table.Column<string>(type: "text", nullable: false),
                    ReasonCodesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyDecisions", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "Principals",
                columns: table => new
                {
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Principals", x => x.PrincipalId);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    TakenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProfileJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransactionEvents",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    CustomerId = table.Column<string>(type: "text", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: false),
                    BeneficiaryId = table.Column<string>(type: "text", nullable: true),
                    BeneficiaryCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WasRefunded = table.Column<bool>(type: "boolean", nullable: false),
                    PriorFailedAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionEvents", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "TransactionIntents",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Merchant = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionIntents", x => x.TransactionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_TransactionId",
                table: "Approvals",
                column: "TransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_TransactionId",
                table: "AuditRecords",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Authorities_AgentId",
                table: "Authorities",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSnapshots_EntityId",
                table: "ProfileSnapshots",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvents_CustomerId",
                table: "TransactionEvents",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvents_MerchantId",
                table: "TransactionEvents",
                column: "MerchantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Approvals");

            migrationBuilder.DropTable(
                name: "AuditRecords");

            migrationBuilder.DropTable(
                name: "Authorities");

            migrationBuilder.DropTable(
                name: "Bindings");

            migrationBuilder.DropTable(
                name: "EvidenceManifests");

            migrationBuilder.DropTable(
                name: "Merchants");

            migrationBuilder.DropTable(
                name: "PaymentOutcomes");

            migrationBuilder.DropTable(
                name: "PolicyDecisions");

            migrationBuilder.DropTable(
                name: "Principals");

            migrationBuilder.DropTable(
                name: "ProfileSnapshots");

            migrationBuilder.DropTable(
                name: "TransactionEvents");

            migrationBuilder.DropTable(
                name: "TransactionIntents");
        }
    }
}
