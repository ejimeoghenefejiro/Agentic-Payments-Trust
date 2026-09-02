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
