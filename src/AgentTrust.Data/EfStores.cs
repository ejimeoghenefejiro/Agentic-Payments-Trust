using System.Text.Json;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

/// <summary>
/// EF-Core-backed implementations of every AgentTrust.Core store interface, targeting
/// PostgreSQL in production (SQLite in tests — see AgentTrustDbContextFactory). Nested
/// collections are round-tripped as JSON text via System.Text.Json rather than normalised
/// child tables, matching how the domain treats them as atomic payloads. The in-memory
/// implementations in AgentTrust.Core remain available and are what unit tests use by default.
/// </summary>
public sealed class EfAgentRegistry : IAgentRegistry
{
    private readonly AgentTrustDbContext _db;
    public EfAgentRegistry(AgentTrustDbContext db) => _db = db;

    public AgentIdentity? Find(string agentId)
    {
        var e = _db.Agents.AsNoTracking().FirstOrDefault(a => a.AgentId == agentId);
        return e is null ? null : ToDomain(e);
    }

    public void Register(AgentIdentity identity)
    {
        var existing = _db.Agents.Find(identity.AgentId);
        if (existing is null)
        {
            _db.Agents.Add(ToEntity(identity));
        }
        else
        {
            existing.PrincipalId = identity.PrincipalId;
            existing.AgentType = identity.AgentType;
            existing.Environment = identity.Environment;
            existing.CredentialStatus = identity.CredentialStatus.ToString();
            existing.IssuedAt = identity.IssuedAt;
            existing.ExpiresAt = identity.ExpiresAt;
            existing.IssuerTrustAnchor = identity.IssuerTrustAnchor;
        }
        _db.SaveChanges();
    }

    private static AgentIdentity ToDomain(AgentEntity e) => new(
        e.AgentId, e.PrincipalId, e.AgentType, e.Environment,
        Enum.Parse<CredentialStatus>(e.CredentialStatus), e.IssuedAt, e.ExpiresAt, e.IssuerTrustAnchor);

    private static AgentEntity ToEntity(AgentIdentity a) => new()
    {
        AgentId = a.AgentId,
        PrincipalId = a.PrincipalId,
        AgentType = a.AgentType,
        Environment = a.Environment,
        CredentialStatus = a.CredentialStatus.ToString(),
        IssuedAt = a.IssuedAt,
        ExpiresAt = a.ExpiresAt,
        IssuerTrustAnchor = a.IssuerTrustAnchor
    };
}

public sealed class EfPrincipalStore : IPrincipalStore
{
    private readonly AgentTrustDbContext _db;
    public EfPrincipalStore(AgentTrustDbContext db) => _db = db;

    public Principal? Find(string principalId)
    {
        var e = _db.Principals.AsNoTracking().FirstOrDefault(p => p.PrincipalId == principalId);
        return e is null ? null : new Principal(e.PrincipalId, e.Name, e.RegisteredAt);
    }

    public void Register(Principal principal)
    {
        var existing = _db.Principals.Find(principal.PrincipalId);
        if (existing is null)
        {
            _db.Principals.Add(new PrincipalEntity { PrincipalId = principal.PrincipalId, Name = principal.Name, RegisteredAt = principal.RegisteredAt });
        }
        else
        {
            existing.Name = principal.Name;
        }
        _db.SaveChanges();
    }
}

public sealed class EfMerchantStore : IMerchantStore
{
    private readonly AgentTrustDbContext _db;
    public EfMerchantStore(AgentTrustDbContext db) => _db = db;

    public Merchant? Find(string merchantId)
    {
        var e = _db.Merchants.AsNoTracking().FirstOrDefault(m => m.MerchantId == merchantId);
        return e is null ? null : new Merchant(e.MerchantId, e.Name, e.Category, e.Approved);
    }

    public IReadOnlyList<Merchant> All() =>
        _db.Merchants.AsNoTracking().Select(e => new Merchant(e.MerchantId, e.Name, e.Category, e.Approved)).ToList();

    public void Register(Merchant merchant)
    {
        var existing = _db.Merchants.Find(merchant.MerchantId);
        if (existing is null)
        {
            _db.Merchants.Add(new MerchantEntity { MerchantId = merchant.MerchantId, Name = merchant.Name, Category = merchant.Category, Approved = merchant.Approved });
        }
        else
        {
            existing.Name = merchant.Name;
            existing.Category = merchant.Category;
            existing.Approved = merchant.Approved;
        }
        _db.SaveChanges();
    }
}

public sealed class EfPrincipalBindingStore : IPrincipalBindingStore
{
    private readonly AgentTrustDbContext _db;
    public EfPrincipalBindingStore(AgentTrustDbContext db) => _db = db;

    public PrincipalBinding? Find(string agentId)
    {
        var e = _db.Bindings.AsNoTracking().FirstOrDefault(b => b.AgentId == agentId);
        return e is null ? null : new PrincipalBinding(e.AgentId, e.PrincipalId, e.BoundAt, e.Active, e.BindingEvidenceRef);
    }

    public void Bind(PrincipalBinding binding)
    {
        var existing = _db.Bindings.Find(binding.AgentId);
        if (existing is null)
        {
            _db.Bindings.Add(new PrincipalBindingEntity
            {
                AgentId = binding.AgentId,
                PrincipalId = binding.PrincipalId,
                BoundAt = binding.BoundAt,
                Active = binding.Active,
                BindingEvidenceRef = binding.BindingEvidenceRef
            });
        }
        else
        {
            existing.PrincipalId = binding.PrincipalId;
            existing.BoundAt = binding.BoundAt;
            existing.Active = binding.Active;
            existing.BindingEvidenceRef = binding.BindingEvidenceRef;
        }
        _db.SaveChanges();
    }
}

public sealed class EfDelegatedAuthorityStore : IDelegatedAuthorityStore
{
    private readonly AgentTrustDbContext _db;
    public EfDelegatedAuthorityStore(AgentTrustDbContext db) => _db = db;

    public DelegatedAuthority? FindByAgent(string agentId)
    {
        var e = _db.Authorities.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .OrderByDescending(a => a.Expiry)
            .FirstOrDefault();
        return e is null ? null : ToDomain(e);
    }

    public DelegatedAuthority? FindById(string authorityId)
    {
        var e = _db.Authorities.AsNoTracking().FirstOrDefault(a => a.AuthorityId == authorityId);
        return e is null ? null : ToDomain(e);
    }

    public void Grant(DelegatedAuthority authority)
    {
        var existing = _db.Authorities.Find(authority.AuthorityId);
        if (existing is null)
        {
            _db.Authorities.Add(ToEntity(authority));
        }
        else
        {
            var updated = ToEntity(authority);
            _db.Entry(existing).CurrentValues.SetValues(updated);
        }
        _db.SaveChanges();
    }

    public void Revoke(string authorityId)
    {
        var existing = _db.Authorities.Find(authorityId);
        if (existing is not null)
        {
            existing.Revoked = true;
            _db.SaveChanges();
        }
    }

    private static DelegatedAuthority ToDomain(DelegatedAuthorityEntity e) => new(
        e.AuthorityId, e.AgentId, e.Permissions, e.PerTransactionLimit, e.DailyLimit,
        e.ApprovedMerchants, e.CategoryScope, e.GeographicScope, e.WindowStart, e.WindowEnd,
        e.HumanApprovalAbove, e.Expiry, e.Revoked);

    private static DelegatedAuthorityEntity ToEntity(DelegatedAuthority a) => new()
    {
        AuthorityId = a.AuthorityId,
        AgentId = a.AgentId,
        Permissions = a.Permissions.ToList(),
        PerTransactionLimit = a.PerTransactionLimit,
        DailyLimit = a.DailyLimit,
        ApprovedMerchants = a.ApprovedMerchants.ToList(),
        CategoryScope = a.CategoryScope.ToList(),
        GeographicScope = a.GeographicScope,
        WindowStart = a.WindowStart,
        WindowEnd = a.WindowEnd,
        HumanApprovalAbove = a.HumanApprovalAbove,
        Expiry = a.Expiry,
        Revoked = a.Revoked
    };
}

public sealed class EfTransactionLedger : ITransactionLedger
{
    private readonly AgentTrustDbContext _db;
    public EfTransactionLedger(AgentTrustDbContext db) => _db = db;

    public decimal AmountSpentToday(string agentId, DateOnly day)
    {
        // Both DateOnly.FromDateTime(...) and a DateTimeOffset range comparison inside the
        // query fail to translate on at least one supported provider (SQL Server rejects the
        // former outright; SQLite's provider has no native DateTimeOffset comparison support
        // and rejects the latter). Materialising the agent-scoped rows first and filtering by
        // date/decision in memory sidesteps provider-specific SQL translation entirely, at the
        // cost of pulling one agent's transaction history into memory per policy check —
        // acceptable at this prototype's scale.
        var intents = _db.TransactionIntents.AsNoTracking().Where(i => i.AgentId == agentId).ToList();
        if (intents.Count == 0) return 0m;

        var transactionIds = intents.Select(i => i.TransactionId).ToList();
        var approvedIds = _db.PolicyDecisions.AsNoTracking()
            .Where(d => transactionIds.Contains(d.TransactionId) && d.Decision == nameof(Decision.Approve))
            .Select(d => d.TransactionId)
            .ToHashSet();

        return intents
            .Where(i => approvedIds.Contains(i.TransactionId) && DateOnly.FromDateTime(i.RequestedAt.UtcDateTime) == day)
            .Sum(i => i.Amount);
    }

    public bool IsDuplicate(string agentId, string? idempotencyKey)
    {
        if (idempotencyKey is null) return false;
        return _db.TransactionIntents.AsNoTracking()
            .Where(i => i.AgentId == agentId && i.IdempotencyKey == idempotencyKey)
            .Join(_db.PolicyDecisions.AsNoTracking(), i => i.TransactionId, d => d.TransactionId, (i, d) => d.Decision)
            .Any(decision => decision == nameof(Decision.Approve));
    }

    public void Record(TransactionIntent intent, Decision decision)
    {
        // Intent + decision are persisted by ITransactionIntentStore / IPolicyDecisionStore in the
        // same TrustFramework call; this ledger only needs to read them back for aggregation, so
        // Record is a no-op here to avoid double-writing the same rows.
    }
}

public sealed class EfTransactionIntentStore : ITransactionIntentStore
{
    private readonly AgentTrustDbContext _db;
    public EfTransactionIntentStore(AgentTrustDbContext db) => _db = db;

    public void Save(TransactionIntent intent)
    {
        var existing = _db.TransactionIntents.Find(intent.TransactionId);
        var entity = new TransactionIntentEntity
        {
            TransactionId = intent.TransactionId,
            AgentId = intent.AgentId,
            PrincipalId = intent.PrincipalId,
            Action = intent.Action,
            Merchant = intent.Merchant,
            Category = intent.Category,
            Amount = intent.Amount,
            Reason = intent.Reason,
            EvidenceJson = JsonSerializer.Serialize(intent.Evidence),
            RequestedAt = intent.RequestedAt,
            IdempotencyKey = intent.IdempotencyKey
        };
        if (existing is null) _db.TransactionIntents.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public TransactionIntent? Find(string transactionId)
    {
        var e = _db.TransactionIntents.AsNoTracking().FirstOrDefault(i => i.TransactionId == transactionId);
        if (e is null) return null;
        var evidence = JsonSerializer.Deserialize<List<EvidenceItem>>(e.EvidenceJson) ?? new();
        return new TransactionIntent(e.TransactionId, e.AgentId, e.PrincipalId, e.Action, e.Merchant, e.Category, e.Amount, e.Reason, evidence, e.RequestedAt, e.IdempotencyKey);
    }
}

public sealed class EfEvidenceManifestStore : IEvidenceManifestStore
{
    private readonly AgentTrustDbContext _db;
    public EfEvidenceManifestStore(AgentTrustDbContext db) => _db = db;

    public void Save(EvidenceManifest manifest)
    {
        var existing = _db.EvidenceManifests.Find(manifest.TransactionId);
        var entity = new EvidenceManifestEntity
        {
            TransactionId = manifest.TransactionId,
            CitedEvidenceJson = JsonSerializer.Serialize(manifest.CitedEvidence),
            RequiredEvidenceTypesJson = JsonSerializer.Serialize(manifest.RequiredEvidenceTypes)
        };
        if (existing is null) _db.EvidenceManifests.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public EvidenceManifest? Find(string transactionId)
    {
        var e = _db.EvidenceManifests.AsNoTracking().FirstOrDefault(m => m.TransactionId == transactionId);
        if (e is null) return null;
        var cited = JsonSerializer.Deserialize<List<EvidenceItem>>(e.CitedEvidenceJson) ?? new();
        var required = JsonSerializer.Deserialize<List<string>>(e.RequiredEvidenceTypesJson) ?? new();
        return new EvidenceManifest(e.TransactionId, cited, required);
    }
}

public sealed class EfPolicyDecisionStore : IPolicyDecisionStore
{
    private readonly AgentTrustDbContext _db;
    public EfPolicyDecisionStore(AgentTrustDbContext db) => _db = db;

    public void Save(PolicyDecisionResult decision)
    {
        var existing = _db.PolicyDecisions.Find(decision.TransactionId);
        var entity = new PolicyDecisionEntity
        {
            TransactionId = decision.TransactionId,
            Decision = decision.Decision.ToString(),
            ChecksJson = JsonSerializer.Serialize(decision.Checks),
            PolicyVersion = decision.PolicyVersion,
            ReasonCodesJson = JsonSerializer.Serialize(decision.ReasonCodes)
        };
        if (existing is null) _db.PolicyDecisions.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public PolicyDecisionResult? Find(string transactionId)
    {
        var e = _db.PolicyDecisions.AsNoTracking().FirstOrDefault(d => d.TransactionId == transactionId);
        if (e is null) return null;
        var checks = JsonSerializer.Deserialize<List<PolicyCheck>>(e.ChecksJson) ?? new();
        var reasons = JsonSerializer.Deserialize<List<string>>(e.ReasonCodesJson) ?? new();
        return new PolicyDecisionResult(e.TransactionId, Enum.Parse<Decision>(e.Decision), checks, e.PolicyVersion, reasons);
    }
}

public sealed class EfPaymentOutcomeStore : IPaymentOutcomeStore
{
    private readonly AgentTrustDbContext _db;
    public EfPaymentOutcomeStore(AgentTrustDbContext db) => _db = db;

    public void Save(PaymentResult result)
    {
        var existing = _db.PaymentOutcomes.Find(result.TransactionId);
        var entity = new PaymentOutcomeEntity
        {
            TransactionId = result.TransactionId,
            Status = result.Status.ToString(),
            ProviderReference = result.ProviderReference,
            FailureReason = result.FailureReason
        };
        if (existing is null) _db.PaymentOutcomes.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public PaymentResult? Find(string transactionId)
    {
        var e = _db.PaymentOutcomes.AsNoTracking().FirstOrDefault(p => p.TransactionId == transactionId);
        return e is null ? null : new PaymentResult(e.TransactionId, Enum.Parse<PaymentStatus>(e.Status), e.ProviderReference, e.FailureReason);
    }
}

public sealed class EfApprovalStore : IApprovalStore
{
    private readonly AgentTrustDbContext _db;
    public EfApprovalStore(AgentTrustDbContext db) => _db = db;

    public void Save(ApprovalRequest request)
    {
        var existing = _db.Approvals.FirstOrDefault(a => a.TransactionId == request.TransactionId);
        var entity = new ApprovalRequestEntity
        {
            ApprovalId = request.ApprovalId,
            TransactionId = request.TransactionId,
            Status = request.Status.ToString(),
            CreatedAt = request.CreatedAt,
            OriginalDecision = request.OriginalDecision.ToString(),
            Approver = request.Approver,
            DecidedAt = request.DecidedAt,
            Reason = request.Reason,
            FinalOutcome = request.FinalOutcome?.ToString()
        };
        if (existing is null) _db.Approvals.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public ApprovalRequest? Find(string transactionId)
    {
        var e = _db.Approvals.AsNoTracking().FirstOrDefault(a => a.TransactionId == transactionId);
        if (e is null) return null;
        return new ApprovalRequest(
            e.ApprovalId, e.TransactionId, Enum.Parse<ApprovalStatus>(e.Status), e.CreatedAt,
            Enum.Parse<Decision>(e.OriginalDecision), e.Approver, e.DecidedAt, e.Reason,
            e.FinalOutcome is null ? null : Enum.Parse<Decision>(e.FinalOutcome));
    }
}
