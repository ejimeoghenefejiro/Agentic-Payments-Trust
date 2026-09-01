using AgentTrust.Core.Models;

namespace AgentTrust.Core;

public interface IAgentRegistry
{
    AgentIdentity? Find(string agentId);
    void Register(AgentIdentity identity);
}

public interface IPrincipalBindingStore
{
    PrincipalBinding? Find(string agentId);
    void Bind(PrincipalBinding binding);
}

public interface IDelegatedAuthorityStore
{
    DelegatedAuthority? FindByAgent(string agentId);
    DelegatedAuthority? FindById(string authorityId);
    void Grant(DelegatedAuthority authority);
    void Revoke(string authorityId);
}

public interface ITransactionLedger
{
    decimal AmountSpentToday(string agentId, DateOnly day);
    bool IsDuplicate(string agentId, string? idempotencyKey);
    void Record(TransactionIntent intent, Decision decision);
}

public interface IPrincipalStore
{
    Principal? Find(string principalId);
    void Register(Principal principal);
}

public interface IMerchantStore
{
    Merchant? Find(string merchantId);
    IReadOnlyList<Merchant> All();
    void Register(Merchant merchant);
}

/// <summary>Persists every submitted TransactionIntent, regardless of outcome, so an
/// escalated transaction can be resumed once a human approves it.</summary>
public interface ITransactionIntentStore
{
    void Save(TransactionIntent intent);
    TransactionIntent? Find(string transactionId);
}

public interface IEvidenceManifestStore
{
    void Save(EvidenceManifest manifest);
    EvidenceManifest? Find(string transactionId);
}

public interface IPolicyDecisionStore
{
    void Save(PolicyDecisionResult decision);
    PolicyDecisionResult? Find(string transactionId);
}

public interface IPaymentOutcomeStore
{
    void Save(PaymentResult result);
    PaymentResult? Find(string transactionId);
}

public interface IApprovalStore
{
    void Save(ApprovalRequest request);
    ApprovalRequest? Find(string transactionId);
}

public sealed class InMemoryAgentRegistry : IAgentRegistry
{
    private readonly Dictionary<string, AgentIdentity> _agents = new();
    public AgentIdentity? Find(string agentId) => _agents.GetValueOrDefault(agentId);
    public void Register(AgentIdentity identity) => _agents[identity.AgentId] = identity;
}

public sealed class InMemoryPrincipalBindingStore : IPrincipalBindingStore
{
    private readonly Dictionary<string, PrincipalBinding> _bindings = new();
    public PrincipalBinding? Find(string agentId) => _bindings.GetValueOrDefault(agentId);
    public void Bind(PrincipalBinding binding) => _bindings[binding.AgentId] = binding;
}

public sealed class InMemoryDelegatedAuthorityStore : IDelegatedAuthorityStore
{
    private readonly Dictionary<string, DelegatedAuthority> _byAuthorityId = new();
    private readonly Dictionary<string, string> _agentToAuthority = new();

    public DelegatedAuthority? FindByAgent(string agentId) =>
        _agentToAuthority.TryGetValue(agentId, out var authorityId)
            ? _byAuthorityId.GetValueOrDefault(authorityId)
            : null;

    public DelegatedAuthority? FindById(string authorityId) => _byAuthorityId.GetValueOrDefault(authorityId);

    public void Grant(DelegatedAuthority authority)
    {
        _byAuthorityId[authority.AuthorityId] = authority;
        _agentToAuthority[authority.AgentId] = authority.AuthorityId;
    }

    public void Revoke(string authorityId)
    {
        if (_byAuthorityId.TryGetValue(authorityId, out var authority))
        {
            _byAuthorityId[authorityId] = authority with { Revoked = true };
        }
    }
}

public sealed class InMemoryTransactionLedger : ITransactionLedger
{
    private readonly List<(TransactionIntent Intent, Decision Decision)> _records = new();

    public decimal AmountSpentToday(string agentId, DateOnly day) =>
        _records
            .Where(r => r.Intent.AgentId == agentId
                        && r.Decision == Decision.Approve
                        && DateOnly.FromDateTime(r.Intent.RequestedAt.UtcDateTime) == day)
            .Sum(r => r.Intent.Amount);

    public bool IsDuplicate(string agentId, string? idempotencyKey) =>
        idempotencyKey is not null &&
        _records.Any(r => r.Intent.AgentId == agentId
                           && r.Intent.IdempotencyKey == idempotencyKey
                           && r.Decision == Decision.Approve);

    public void Record(TransactionIntent intent, Decision decision) => _records.Add((intent, decision));
}

public sealed class InMemoryPrincipalStore : IPrincipalStore
{
    private readonly Dictionary<string, Principal> _principals = new();
    public Principal? Find(string principalId) => _principals.GetValueOrDefault(principalId);
    public void Register(Principal principal) => _principals[principal.PrincipalId] = principal;
}

public sealed class InMemoryMerchantStore : IMerchantStore
{
    private readonly Dictionary<string, Merchant> _merchants = new();
    public Merchant? Find(string merchantId) => _merchants.GetValueOrDefault(merchantId);
    public IReadOnlyList<Merchant> All() => _merchants.Values.ToList();
    public void Register(Merchant merchant) => _merchants[merchant.MerchantId] = merchant;
}

public sealed class InMemoryTransactionIntentStore : ITransactionIntentStore
{
    private readonly Dictionary<string, TransactionIntent> _intents = new();
    public void Save(TransactionIntent intent) => _intents[intent.TransactionId] = intent;
    public TransactionIntent? Find(string transactionId) => _intents.GetValueOrDefault(transactionId);
}

public sealed class InMemoryEvidenceManifestStore : IEvidenceManifestStore
{
    private readonly Dictionary<string, EvidenceManifest> _manifests = new();
    public void Save(EvidenceManifest manifest) => _manifests[manifest.TransactionId] = manifest;
    public EvidenceManifest? Find(string transactionId) => _manifests.GetValueOrDefault(transactionId);
}

public sealed class InMemoryPolicyDecisionStore : IPolicyDecisionStore
{
    private readonly Dictionary<string, PolicyDecisionResult> _decisions = new();
    public void Save(PolicyDecisionResult decision) => _decisions[decision.TransactionId] = decision;
    public PolicyDecisionResult? Find(string transactionId) => _decisions.GetValueOrDefault(transactionId);
}

public sealed class InMemoryPaymentOutcomeStore : IPaymentOutcomeStore
{
    private readonly Dictionary<string, PaymentResult> _results = new();
    public void Save(PaymentResult result) => _results[result.TransactionId] = result;
    public PaymentResult? Find(string transactionId) => _results.GetValueOrDefault(transactionId);
}

public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly Dictionary<string, ApprovalRequest> _approvals = new();
    public void Save(ApprovalRequest request) => _approvals[request.TransactionId] = request;
    public ApprovalRequest? Find(string transactionId) => _approvals.GetValueOrDefault(transactionId);
}
