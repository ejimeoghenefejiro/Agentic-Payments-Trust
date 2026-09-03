using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

public sealed class AgentTrustDbContext : DbContext
{
    public AgentTrustDbContext(DbContextOptions<AgentTrustDbContext> options) : base(options) { }

    public DbSet<AgentEntity> Agents => Set<AgentEntity>();
    public DbSet<PrincipalEntity> Principals => Set<PrincipalEntity>();
    public DbSet<MerchantEntity> Merchants => Set<MerchantEntity>();
    public DbSet<PrincipalBindingEntity> Bindings => Set<PrincipalBindingEntity>();
    public DbSet<DelegatedAuthorityEntity> Authorities => Set<DelegatedAuthorityEntity>();
    public DbSet<TransactionIntentEntity> TransactionIntents => Set<TransactionIntentEntity>();
    public DbSet<EvidenceManifestEntity> EvidenceManifests => Set<EvidenceManifestEntity>();
    public DbSet<PolicyDecisionEntity> PolicyDecisions => Set<PolicyDecisionEntity>();
    public DbSet<PaymentOutcomeEntity> PaymentOutcomes => Set<PaymentOutcomeEntity>();
    public DbSet<ApprovalRequestEntity> Approvals => Set<ApprovalRequestEntity>();
    public DbSet<AuditRecordEntity> AuditRecords => Set<AuditRecordEntity>();
    public DbSet<TransactionEventEntity> TransactionEvents => Set<TransactionEventEntity>();
    public DbSet<ProfileSnapshotEntity> ProfileSnapshots => Set<ProfileSnapshotEntity>();
    public DbSet<InvestigationStateEntity> InvestigationStates => Set<InvestigationStateEntity>();
    public DbSet<SemanticCaseEntity> SemanticCases => Set<SemanticCaseEntity>();
    public DbSet<DecisionFeedbackEntity> DecisionFeedback => Set<DecisionFeedbackEntity>();
    public DbSet<ConsumerProfileEntity> ConsumerProfiles => Set<ConsumerProfileEntity>();
    public DbSet<ConnectedServiceEntity> ConnectedServices => Set<ConnectedServiceEntity>();
    public DbSet<ConsumerPurchaseTaskEntity> ConsumerPurchaseTasks => Set<ConsumerPurchaseTaskEntity>();
    public DbSet<PurchaseExecutionEntity> PurchaseExecutions => Set<PurchaseExecutionEntity>();
    public DbSet<PurchaseIntentEntity> PurchaseIntents => Set<PurchaseIntentEntity>();
    public DbSet<PurchaseAuthorisationEntity> PurchaseAuthorisations => Set<PurchaseAuthorisationEntity>();
    public DbSet<PurchaseLifecycleEventEntity> PurchaseLifecycleEvents => Set<PurchaseLifecycleEventEntity>();
    public DbSet<PendingConsumerApprovalEntity> PendingConsumerApprovals => Set<PendingConsumerApprovalEntity>();
    public DbSet<CheckoutExecutionEntity> CheckoutExecutions => Set<CheckoutExecutionEntity>();
    public DbSet<ConsumerPaymentAttemptEntity> ConsumerPaymentAttempts => Set<ConsumerPaymentAttemptEntity>();
    public DbSet<TaskOccurrenceEntity> TaskOccurrences => Set<TaskOccurrenceEntity>();
    public DbSet<SpendReservationEntity> SpendReservations => Set<SpendReservationEntity>();
    public DbSet<OneOffAuthorisationEntity> OneOffAuthorisations => Set<OneOffAuthorisationEntity>();
    public DbSet<StripeWebhookEventEntity> StripeWebhookEvents => Set<StripeWebhookEventEntity>();
    public DbSet<PurchaseReceiptEntity> PurchaseReceipts => Set<PurchaseReceiptEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentEntity>().HasKey(e => e.AgentId);
        modelBuilder.Entity<PrincipalEntity>().HasKey(e => e.PrincipalId);
        modelBuilder.Entity<MerchantEntity>().HasKey(e => e.MerchantId);
        modelBuilder.Entity<PrincipalBindingEntity>().HasKey(e => e.AgentId);

        modelBuilder.Entity<DelegatedAuthorityEntity>(b =>
        {
            b.HasKey(e => e.AuthorityId);
            b.HasIndex(e => e.AgentId);
            b.Property(e => e.Permissions).HasConversion(JsonConverters.StringList, JsonConverters.StringListComparer);
            b.Property(e => e.ApprovedMerchants).HasConversion(JsonConverters.StringList, JsonConverters.StringListComparer);
            b.Property(e => e.CategoryScope).HasConversion(JsonConverters.StringList, JsonConverters.StringListComparer);
            b.Property(e => e.PerTransactionLimit).HasPrecision(18, 2);
            b.Property(e => e.DailyLimit).HasPrecision(18, 2);
            b.Property(e => e.HumanApprovalAbove).HasPrecision(18, 2);
        });

        modelBuilder.Entity<TransactionIntentEntity>(b =>
        {
            b.HasKey(e => e.TransactionId);
            b.Property(e => e.Amount).HasPrecision(18, 2);
        });
        modelBuilder.Entity<EvidenceManifestEntity>().HasKey(e => e.TransactionId);
        modelBuilder.Entity<PolicyDecisionEntity>().HasKey(e => e.TransactionId);
        modelBuilder.Entity<PaymentOutcomeEntity>().HasKey(e => e.TransactionId);

        modelBuilder.Entity<ApprovalRequestEntity>(b =>
        {
            b.HasKey(e => e.ApprovalId);
            b.HasIndex(e => e.TransactionId).IsUnique();
        });

        modelBuilder.Entity<AuditRecordEntity>(b =>
        {
            b.HasKey(e => e.SequenceNumber);
            b.Property(e => e.SequenceNumber).ValueGeneratedNever();
            b.HasIndex(e => e.TransactionId);
        });

        modelBuilder.Entity<TransactionEventEntity>(b =>
        {
            b.HasKey(e => e.TransactionId);
            b.HasIndex(e => e.CustomerId);
            b.HasIndex(e => e.MerchantId);
            b.Property(e => e.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<ProfileSnapshotEntity>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => e.EntityId);
        });

        modelBuilder.Entity<InvestigationStateEntity>(b =>
        {
            b.HasKey(e => e.InvestigationId);
            b.HasIndex(e => e.TransactionId);
            b.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<SemanticCaseEntity>(b =>
        {
            b.HasKey(e => e.CaseId);
            b.HasIndex(e => e.ScopeId);
            b.HasIndex(e => e.UpdatedAt);
        });

        modelBuilder.Entity<DecisionFeedbackEntity>(b =>
        {
            b.HasKey(e => e.TransactionId);
            b.HasIndex(e => e.InvestigationId);
            b.HasIndex(e => e.ValidationStatus);
        });

        ConfigureConsumerCommerce(modelBuilder);
    }

    private static void ConfigureConsumerCommerce(ModelBuilder modelBuilder)
    {
        Configure<ConsumerProfileEntity>(modelBuilder, x => x.PrincipalId);
        Configure<ConnectedServiceEntity>(modelBuilder, x => x.Id);
        modelBuilder.Entity<ConnectedServiceEntity>().HasIndex(x => new { x.PrincipalId, x.Provider, x.ExternalAccountReference }).IsUnique();

        Configure<ConsumerPurchaseTaskEntity>(modelBuilder, x => x.TaskId);
        modelBuilder.Entity<ConsumerPurchaseTaskEntity>(b =>
        {
            b.HasIndex(x => new { x.PrincipalId, x.Status });
            b.HasIndex(x => x.NextExecutionAt);
            b.Property(x => x.MaximumAmount).HasPrecision(18, 2);
        });

        Configure<PurchaseExecutionEntity>(modelBuilder, x => x.ExecutionId);
        modelBuilder.Entity<PurchaseExecutionEntity>(b =>
        {
            b.HasIndex(x => new { x.TaskId, x.ScheduledFor }).IsUnique();
            b.HasIndex(x => x.PurchaseIntentId).IsUnique();
            b.HasIndex(x => x.ProviderPaymentId).IsUnique();
            b.HasIndex(x => new { x.PrincipalId, x.State });
        });

        Configure<PurchaseIntentEntity>(modelBuilder, x => x.PurchaseIntentId);
        modelBuilder.Entity<PurchaseIntentEntity>(b =>
        {
            b.HasIndex(x => x.ExecutionId).IsUnique();
            b.HasIndex(x => x.PaymentIdempotencyKey).IsUnique();
            b.HasIndex(x => new { x.PrincipalId, x.CreatedAt });
            Money(b.Property(x => x.Subtotal)); Money(b.Property(x => x.DeliveryFee)); Money(b.Property(x => x.TotalAmount));
        });

        Configure<PurchaseAuthorisationEntity>(modelBuilder, x => x.AuthorisationId);
        modelBuilder.Entity<PurchaseAuthorisationEntity>(b =>
        {
            b.HasIndex(x => x.PurchaseIntentId).IsUnique();
            b.HasIndex(x => new { x.Status, x.ExpiresAt });
            Money(b.Property(x => x.AuthorisedAmount));
        });

        modelBuilder.Entity<PurchaseLifecycleEventEntity>(b =>
        {
            b.HasKey(x => x.SequenceNumber); b.Property(x => x.SequenceNumber).ValueGeneratedOnAdd();
            b.HasIndex(x => x.EventId).IsUnique(); b.HasIndex(x => new { x.PurchaseIntentId, x.SequenceNumber });
        });

        Configure<PendingConsumerApprovalEntity>(modelBuilder, x => x.ApprovalId);
        modelBuilder.Entity<PendingConsumerApprovalEntity>(b =>
        {
            b.HasIndex(x => x.PurchaseIntentId).IsUnique(); b.HasIndex(x => new { x.PrincipalId, x.Status });
            Money(b.Property(x => x.Amount));
        });

        Configure<CheckoutExecutionEntity>(modelBuilder, x => x.CheckoutExecutionId);
        modelBuilder.Entity<CheckoutExecutionEntity>(b =>
        { b.HasIndex(x => x.PurchaseIntentId).IsUnique(); b.HasIndex(x => x.PaymentIdempotencyKey).IsUnique(); });

        Configure<ConsumerPaymentAttemptEntity>(modelBuilder, x => x.PaymentAttemptId);
        modelBuilder.Entity<ConsumerPaymentAttemptEntity>(b =>
        {
            b.ToTable("ConsumerPaymentAttempts");
            b.HasIndex(x => x.PaymentIdempotencyKey).IsUnique(); b.HasIndex(x => x.ProviderPaymentId).IsUnique();
            b.HasIndex(x => new { x.LatestStatus, x.UpdatedAt });
        });

        Configure<TaskOccurrenceEntity>(modelBuilder, x => x.OccurrenceId);
        modelBuilder.Entity<TaskOccurrenceEntity>(b =>
        { b.HasIndex(x => new { x.TaskId, x.ScheduledFor }).IsUnique(); b.HasIndex(x => new { x.Status, x.LeaseExpiresAt }); });

        Configure<SpendReservationEntity>(modelBuilder, x => x.ReservationId);
        modelBuilder.Entity<SpendReservationEntity>(b =>
        { b.HasIndex(x => x.ExecutionId).IsUnique(); b.HasIndex(x => new { x.MandateId, x.Status, x.ReservedAt }); Money(b.Property(x => x.Amount)); });

        Configure<OneOffAuthorisationEntity>(modelBuilder, x => x.AuthorisationId);
        modelBuilder.Entity<OneOffAuthorisationEntity>(b =>
        { b.HasIndex(x => x.PurchaseIntentId).IsUnique(); b.HasIndex(x => x.TransactionFingerprint).IsUnique(); Money(b.Property(x => x.MaximumAmount)); });

        Configure<StripeWebhookEventEntity>(modelBuilder, x => x.ProviderEventId);
        modelBuilder.Entity<StripeWebhookEventEntity>().HasIndex(x => new { x.Status, x.ReceivedAt });

        modelBuilder.Entity<PurchaseReceiptEntity>(b =>
        {
            b.HasKey(x => x.ReceiptId); b.HasIndex(x => x.PurchaseIntentId).IsUnique();
            b.HasIndex(x => x.ProviderPaymentId).IsUnique(); b.HasIndex(x => new { x.PrincipalId, x.PurchasedAt });
            Money(b.Property(x => x.TotalAmount));
        });

        static void Configure<TEntity>(ModelBuilder builder,
            System.Linq.Expressions.Expression<Func<TEntity, object?>> key) where TEntity : class
        {
            builder.Entity<TEntity>().HasKey(key);
            builder.Entity<TEntity>().Property<long>("Version").IsConcurrencyToken();
        }
        static void Money(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<decimal> property) => property.HasPrecision(18, 2);
    }
}

internal static class JsonConverters
{
    public static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>, string> StringList =
        new(
            v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
            v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>());

    public static readonly Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>> StringListComparer = new(
        (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
        v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
        v => v.ToList());
}
