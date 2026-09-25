using AgentTrust.Consumer;
using System.Text.Json;

namespace AgentTrust.Commerce;

/// <summary>A customer outcome that must remain true while live provider facts change.</summary>
public sealed record CommerceGoal(
    string GoalId,
    string SearchTerm,
    int Quantity,
    string? PreferredProductId,
    decimal? MaximumUnitPrice,
    SubstitutionPolicy SubstitutionPolicy,
    bool RequiredForOutcome);

/// <summary>Provider evidence showing how one goal was satisfied.</summary>
public sealed record CommerceGoalProof(
    string GoalId,
    string ProductId,
    int Quantity,
    decimal UnitPrice,
    bool Substituted,
    string Evidence);

public sealed record CommerceGoalCheck(
    bool Passed,
    IReadOnlyList<CommerceGoalProof> Proofs,
    IReadOnlyList<string> Failures);

public enum CommerceOodaStatus { Observing, Orienting, Deciding, Acting, ProposalVerified, Verifying, Completed, NeedsIntervention, Failed }

public sealed record CommerceOodaStep(string StepId,string CycleId,string PrincipalId,int CycleNumber,int Sequence,
    CommerceOodaStatus Phase,string InputJson,string OutputJson,string EvidenceJson,string? DecisionReason,DateTimeOffset CreatedAt);

public sealed record CommerceOodaCycle(
    string CycleId,
    string TaskId,
    string PrincipalId,
    string PurchaseIntentId,
    DateTimeOffset ScheduledFor,
    int CycleNumber,
    CommerceOodaStatus Status,
    string GoalJson,
    string ObservationsJson,
    string AlternativesJson,
    string DecisionJson,
    string ActionJson,
    string ProofJson,
    string? Outcome,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version = 1);

public interface ICommerceOodaCycleStore
{
    CommerceOodaCycle? FindOwned(string purchaseIntentId, string principalId);
    IReadOnlyList<CommerceOodaCycle> FindByTaskOwned(string taskId, string principalId);
    IReadOnlyList<CommerceOodaStep> StepsOwned(string cycleId,string principalId);
    void Append(CommerceOodaStep step);
    void Save(CommerceOodaCycle cycle);
}

public sealed class InMemoryCommerceOodaCycleStore : ICommerceOodaCycleStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CommerceOodaCycle> _cycles = new();
    private readonly List<CommerceOodaStep> _steps = [];
    public CommerceOodaCycle? FindOwned(string purchaseIntentId, string principalId)
    {
        lock (_gate) return _cycles.Values.SingleOrDefault(cycle =>
            cycle.PurchaseIntentId == purchaseIntentId && cycle.PrincipalId == principalId);
    }
    public void Save(CommerceOodaCycle cycle)
    {
        lock (_gate) _cycles[cycle.CycleId] = cycle;
    }
    public IReadOnlyList<CommerceOodaCycle> FindByTaskOwned(string taskId, string principalId)
    {
        lock (_gate) return _cycles.Values
            .Where(cycle => cycle.TaskId == taskId && cycle.PrincipalId == principalId)
            .OrderByDescending(cycle => cycle.ScheduledFor).ToArray();
    }
    public IReadOnlyList<CommerceOodaStep> StepsOwned(string cycleId,string principalId)
    {lock(_gate)return _steps.Where(x=>x.CycleId==cycleId&&x.PrincipalId==principalId).OrderBy(x=>x.CycleNumber).ThenBy(x=>x.Sequence).ToArray();}
    public void Append(CommerceOodaStep step)
    {lock(_gate)if(_steps.All(x=>x.StepId!=step.StepId))_steps.Add(step);}
}

public sealed record CommerceGoalAttempt(
    Basket Basket,
    CommerceGoalProof? Proof,
    IReadOnlyList<Product> ObservedAlternatives);

/// <summary>
/// A bounded Observe-Orient-Decide-Act loop for provider inventory. It may adapt the
/// selected product, but it cannot relax quantity, price, substitution or financial controls.
/// </summary>
public sealed class CommerceGoalLoop
{
    public async Task<CommerceGoalAttempt> SatisfyAsync(
        CommerceGoal goal,
        Basket basket,
        ICommerceConnector connector,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludedProductTerms = null,
        IReadOnlyCollection<string>? preferredProductTerms = null)
    {
        // Observe: obtain current provider inventory, not remembered catalogue data.
        var observed = await connector.SearchProductsAsync(goal.SearchTerm, cancellationToken);

        // Orient: retain only options that satisfy the customer's hard constraints.
        var preferredObserved = goal.PreferredProductId is null
            ? null
            : observed.FirstOrDefault(product => product.ProductId.Equals(goal.PreferredProductId, StringComparison.OrdinalIgnoreCase));
        var substitutionPriceCeiling = goal.SubstitutionPolicy == SubstitutionPolicy.SameOrLowerPrice
            ? preferredObserved?.UnitPrice
            : null;
        var eligible = observed
            .Where(product => product.AvailableQuantity >= goal.Quantity)
            .Where(product => !IsExcluded(product, excludedProductTerms))
            .Where(product => goal.MaximumUnitPrice is null || product.UnitPrice <= goal.MaximumUnitPrice)
            .Where(product => substitutionPriceCeiling is null
                || product.ProductId.Equals(goal.PreferredProductId, StringComparison.OrdinalIgnoreCase)
                || product.UnitPrice <= substitutionPriceCeiling)
            .OrderBy(product => IsExcluded(product, preferredProductTerms) ? 0 : 1)
            .ThenBy(product => product.UnitPrice)
            .ThenBy(product => product.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Decide: prefer the requested item; substitute only when permission exists.
        var preferred = goal.PreferredProductId is null
            ? null
            : eligible.FirstOrDefault(product => product.ProductId.Equals(goal.PreferredProductId, StringComparison.OrdinalIgnoreCase));
        var options = preferred is not null
            ? eligible.Prepend(preferred).DistinctBy(product => product.ProductId, StringComparer.OrdinalIgnoreCase)
            : goal.PreferredProductId is null || goal.SubstitutionPolicy != SubstitutionPolicy.Never ? eligible : [];

        // Act: inventory can change between search and basket update, so try the next valid option.
        foreach (var option in options)
        {
            try
            {
                var updated = await connector.AddBasketItemAsync(
                    basket.BasketId,
                    option.ProductId,
                    goal.Quantity,
                    goal.SubstitutionPolicy != SubstitutionPolicy.Never,
                    cancellationToken);
                var substituted = goal.PreferredProductId is not null
                    && !option.ProductId.Equals(goal.PreferredProductId, StringComparison.OrdinalIgnoreCase);
                return new CommerceGoalAttempt(updated, new CommerceGoalProof(
                    goal.GoalId,
                    option.ProductId,
                    goal.Quantity,
                    option.UnitPrice,
                    substituted,
                    substituted ? "Available provider alternative selected." : "Requested goal matched live provider inventory."), observed);
            }
            catch (InvalidOperationException)
            {
                // Re-orient on the next observed option after a stock race.
            }
        }

        return new CommerceGoalAttempt(basket, null, observed);
    }

    private static bool IsExcluded(Product product, IReadOnlyCollection<string>? excludedTerms)
    {
        if (excludedTerms is null || excludedTerms.Count == 0) return false;
        return excludedTerms.Any(term =>
            !string.IsNullOrWhiteSpace(term) &&
            (product.ProductId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             product.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             product.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))));
    }

    public CommerceGoalCheck Check(
        IReadOnlyList<CommerceGoal> goals,
        IReadOnlyList<CommerceGoalProof> proofs,
        CommerceQuote quote,
        decimal maximumAmount)
    {
        var failures = new List<string>();
        foreach (var goal in goals)
        {
            var proof = proofs.FirstOrDefault(candidate => candidate.GoalId == goal.GoalId);
            if (proof is null && goal.RequiredForOutcome)
            {
                failures.Add($"GOAL_UNSATISFIED:{goal.SearchTerm}");
                continue;
            }

            if (proof is null) continue;

            var quoted = quote.Items.FirstOrDefault(item => item.ProductId == proof.ProductId);
            if (quoted is null || quoted.Quantity < goal.Quantity)
                failures.Add($"GOAL_NOT_PROVEN_BY_QUOTE:{goal.SearchTerm}");
        }

        if (quote.TotalAmount > maximumAmount) failures.Add("USER_BUDGET_EXCEEDED");
        return new CommerceGoalCheck(failures.Count == 0, proofs, failures);
    }
}
