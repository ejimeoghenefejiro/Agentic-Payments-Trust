using AgentTrust.Core;
using AgentTrust.Core.Models;

namespace AgentTrust.Policy;

public sealed class PolicyEngine
{
    public const string PolicyVersion = "procurement-policy-v1";

    private readonly IAgentRegistry _agents;
    private readonly IPrincipalBindingStore _bindings;
    private readonly IDelegatedAuthorityStore _authorities;
    private readonly ITransactionLedger _ledger;

    public PolicyEngine(
        IAgentRegistry agents,
        IPrincipalBindingStore bindings,
        IDelegatedAuthorityStore authorities,
        ITransactionLedger ledger)
    {
        _agents = agents;
        _bindings = bindings;
        _authorities = authorities;
        _ledger = ledger;
    }

    public PolicyDecisionResult Evaluate(TransactionIntent intent, EvidenceManifest evidence, DelegatedAuthority? transactionScopedAuthority = null)
    {
        var checks = new List<PolicyCheck>();
        var reasonCodes = new List<string>();
        var now = intent.RequestedAt;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var identity = _agents.Find(intent.AgentId);
        bool identityValid = identity is not null && identity.IsValid(now);
        checks.Add(new PolicyCheck("IdentityValid", identityValid,
            identityValid ? "Agent credential active" : "Agent credential missing, expired, or revoked"));
        if (!identityValid)
        {
            reasonCodes.Add("IDENTITY_INVALID");
            return Deny(intent, checks, reasonCodes);
        }

        var binding = _bindings.Find(intent.AgentId);
        bool bindingValid = binding is not null && binding.IsValidFor(intent.AgentId, intent.PrincipalId);
        checks.Add(new PolicyCheck("PrincipalBindingValid", bindingValid,
            bindingValid ? "Agent bound to claimed principal" : "No valid principal binding for this agent/principal pair"));
        if (!bindingValid)
        {
            reasonCodes.Add("PRINCIPAL_MISBINDING");
            return Deny(intent, checks, reasonCodes);
        }

        var authority = transactionScopedAuthority is not null && transactionScopedAuthority.AgentId == intent.AgentId
            ? transactionScopedAuthority
            : _authorities.FindByAgent(intent.AgentId);
        bool authorityActive = authority is not null && authority.IsActive(today);
        checks.Add(new PolicyCheck("AuthorityActive", authorityActive,
            authorityActive ? "Delegated authority is active and unexpired" : "Authority missing, revoked, or expired"));
        if (!authorityActive)
        {
            reasonCodes.Add("AUTHORITY_INACTIVE");
            return Deny(intent, checks, reasonCodes);
        }

        bool duplicate = _ledger.IsDuplicate(intent.AgentId, intent.IdempotencyKey);
        checks.Add(new PolicyCheck("NotDuplicate", !duplicate,
            duplicate ? "Transaction already processed (idempotency key match)" : "No duplicate detected"));
        if (duplicate)
        {
            reasonCodes.Add("DUPLICATE_TRANSACTION");
            return Deny(intent, checks, reasonCodes);
        }

        bool actionInScope = authority!.PermitsAction(intent.Action);
        checks.Add(new PolicyCheck("ActionInScope", actionInScope,
            actionInScope ? "Action within delegated permissions" : $"Action '{intent.Action}' not permitted"));
        if (!actionInScope)
        {
            reasonCodes.Add("ACTION_OUT_OF_SCOPE");
            return Deny(intent, checks, reasonCodes);
        }

        bool merchantAllowed = authority.PermitsMerchant(intent.Merchant);
        checks.Add(new PolicyCheck("MerchantAllowed", merchantAllowed,
            merchantAllowed ? "Merchant is within approved scope" : $"Merchant '{intent.Merchant}' not approved"));
        if (!merchantAllowed)
        {
            reasonCodes.Add("MERCHANT_NOT_APPROVED");
            return Escalate(intent, checks, reasonCodes);
        }

        bool withinTransactionLimit = intent.Amount <= authority.PerTransactionLimit;
        checks.Add(new PolicyCheck("WithinTransactionLimit", withinTransactionLimit,
            withinTransactionLimit
                ? $"Amount {intent.Amount} within per-transaction limit {authority.PerTransactionLimit}"
                : $"Amount {intent.Amount} exceeds per-transaction limit {authority.PerTransactionLimit}"));
        if (!withinTransactionLimit)
        {
            reasonCodes.Add("TRANSACTION_LIMIT_EXCEEDED");
            return Deny(intent, checks, reasonCodes);
        }

        if (authority.DailyLimit is decimal dailyLimit)
        {
            var spentToday = _ledger.AmountSpentToday(intent.AgentId, today);
            var withinDailyLimit = spentToday + intent.Amount <= dailyLimit;
            checks.Add(new PolicyCheck("WithinDailyLimit", withinDailyLimit,
                withinDailyLimit
                    ? $"Daily total {spentToday + intent.Amount} within limit {dailyLimit}"
                    : $"Daily total {spentToday + intent.Amount} exceeds limit {dailyLimit}"));
            if (!withinDailyLimit)
            {
                reasonCodes.Add("DAILY_LIMIT_EXCEEDED");
                return Deny(intent, checks, reasonCodes);
            }
        }
        else
        {
            checks.Add(new PolicyCheck("WithinDailyLimit", true,
                "No daily limit configured; mandate reservation controls remain authoritative"));
        }

        bool withinWindow = authority.WithinTimeWindow(TimeOnly.FromDateTime(now.LocalDateTime));
        checks.Add(new PolicyCheck("WithinTimeWindow", withinWindow,
            withinWindow ? "Within permitted time window" : "Outside permitted time window"));
        if (!withinWindow)
        {
            reasonCodes.Add("OUTSIDE_TIME_WINDOW");
            return Escalate(intent, checks, reasonCodes);
        }

        bool evidenceSufficient = evidence.Recall >= 1.0 && evidence.InvalidCitedEvidence.Count == 0;
        checks.Add(new PolicyCheck("EvidenceSufficient", evidenceSufficient,
            evidenceSufficient ? "Required evidence present and valid" : "Evidence missing or invalid"));
        if (!evidenceSufficient)
        {
            reasonCodes.Add("EVIDENCE_INSUFFICIENT");
            return Escalate(intent, checks, reasonCodes);
        }

        bool humanApprovalRequired = authority.RequiresHumanApproval(intent.Amount);
        checks.Add(new PolicyCheck("HumanApprovalRequired", !humanApprovalRequired,
            humanApprovalRequired
                ? $"Amount {intent.Amount} exceeds human-approval threshold {authority.HumanApprovalAbove}"
                : "Below human-approval threshold"));
        if (humanApprovalRequired)
        {
            reasonCodes.Add("HUMAN_APPROVAL_REQUIRED");
            return Escalate(intent, checks, reasonCodes);
        }

        return Approve(intent, checks);
    }

    private PolicyDecisionResult Approve(TransactionIntent intent, List<PolicyCheck> checks) =>
        new(intent.TransactionId, Decision.Approve, checks, PolicyVersion, Array.Empty<string>());

    private PolicyDecisionResult Deny(TransactionIntent intent, List<PolicyCheck> checks, List<string> reasons) =>
        new(intent.TransactionId, Decision.Deny, checks, PolicyVersion, reasons);

    private PolicyDecisionResult Escalate(TransactionIntent intent, List<PolicyCheck> checks, List<string> reasons) =>
        new(intent.TransactionId, Decision.Escalate, checks, PolicyVersion, reasons);
}
