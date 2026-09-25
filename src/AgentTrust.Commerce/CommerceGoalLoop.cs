using AgentTrust.Consumer;

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

/// <summary>
/// A bounded Observe-Orient-Decide-Act loop for provider inventory. It may adapt the
/// selected product, but it cannot relax quantity, price, substitution or financial controls.
/// </summary>
public sealed class CommerceGoalLoop
{
    public async Task<(Basket Basket, CommerceGoalProof? Proof)> SatisfyAsync(
        CommerceGoal goal,
        Basket basket,
        ICommerceConnector connector,
        CancellationToken cancellationToken)
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
            .Where(product => goal.MaximumUnitPrice is null || product.UnitPrice <= goal.MaximumUnitPrice)
            .Where(product => substitutionPriceCeiling is null
                || product.ProductId.Equals(goal.PreferredProductId, StringComparison.OrdinalIgnoreCase)
                || product.UnitPrice <= substitutionPriceCeiling)
            .OrderBy(product => product.UnitPrice)
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
                return (updated, new CommerceGoalProof(
                    goal.GoalId,
                    option.ProductId,
                    goal.Quantity,
                    option.UnitPrice,
                    substituted,
                    substituted ? "Available provider alternative selected." : "Requested goal matched live provider inventory."));
            }
            catch (InvalidOperationException)
            {
                // Re-orient on the next observed option after a stock race.
            }
        }

        return (basket, null);
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
