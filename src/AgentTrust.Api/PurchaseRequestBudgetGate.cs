using System.Text.RegularExpressions;
using AgentTrust.Commerce;

namespace AgentTrust.Api;

/// <summary>
/// Prevents a new purchase from silently inheriting financial limits from an older conversation.
/// A budget-less message may continue an open conversation only when it clearly confirms that
/// conversation. All other purchase requests must state a monetary limit.
/// </summary>
public static partial class PurchaseRequestBudgetGate
{
    public static bool HasExplicitBudget(string instruction) => TryGetExplicitBudget(instruction,out _);

    public static bool TryGetExplicitBudget(string instruction,out decimal? budget)
    {
        budget=null;
        var match=CurrencyBeforeAmount().Match(instruction);
        if(!match.Success)match=AmountBeforeCurrency().Match(instruction);
        if(!match.Success)match=NamedLimit().Match(instruction);
        if(!match.Success)return false;
        var amount=Regex.Match(match.Value,@"\d+(?:\.\d{1,2})?");
        if(!amount.Success||!decimal.TryParse(amount.Value,System.Globalization.NumberStyles.Number,
               System.Globalization.CultureInfo.InvariantCulture,out var parsed)||parsed<=0)return false;
        budget=parsed;return true;
    }

    public static bool IsExplicitContinuation(string instruction) =>
        ContinuationLanguage().IsMatch(instruction)
        || IsBudgetOnlyAnswer(instruction);

    public static bool IsBudgetOnlyAnswer(string instruction) => BudgetOnlyAnswer().IsMatch(instruction.Trim());

    public static bool RefersToOpenProposal(string instruction) => PriorProposalReference().IsMatch(instruction);

    public static ConsumerPurchasePlan Clarification(ObjectiveClarification? suggestion = null)
    {
        var suggestedMeal=suggestion is null||suggestion.SuggestedConcepts.Count==0
            ?"I can suggest a suitable good-value meal after you provide that information."
            :$"I suggest {FriendlyList(suggestion.SuggestedConcepts)} as a good-value meal.";
        var tools=suggestion is null?Array.Empty<string>():[suggestion.Provenance];
        return new(
        PurchasePlanningStatus.NeedsInput,
        "Budget and dietary confirmation needed",
        $"{suggestedMeal} I will not prepare or purchase the basket until you confirm the financial and dietary constraints.",
        0m,
        "GBP",
        [],
        ["What is your maximum budget, do you have any allergies or dietary requirements, and should I go ahead with this suggestion?"],
        null,
        tools,
        InteractionDecision: PurchaseInteractionDecision.Clarify);
    }

    private static string FriendlyList(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "a meal",
        1 => values[0],
        _ => $"{string.Join(", ",values.Take(values.Count-1))} and {values[^1]}"
    };

    [GeneratedRegex(@"(?:£|GBP\s*)(?:\s*)\d+(?:\.\d{1,2})?", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyBeforeAmount();

    [GeneratedRegex(@"\d+(?:\.\d{1,2})?\s*(?:pounds?|GBP)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmountBeforeCurrency();

    [GeneratedRegex(@"\b(?:budget|maximum|max(?:imum)?\s+spend|spend\s+up\s+to|do\s+not\s+spend\s+more\s+than|under|within)\D{0,20}\d+(?:\.\d{1,2})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex NamedLimit();

    [GeneratedRegex(@"\b(?:yes|confirm|confirmed|that\s+(?:suggestion|basket|option)|the\s+suggested|proceed\s+with|use\s+your\s+best\s+judgement\s+and\s+proceed|go\s+ahead|no\s+allerg(?:y|ies)|dietary\s+requirements?|show\s+(?:me\s+)?(?:alternatives?|cheaper\s+options)|change\s+the\s+basket|I\s+already\s+have|I\s+do\s+not\s+have|I\s+don't\s+have|remove|leave\s+out|use\s+.+\s+instead)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationLanguage();

    [GeneratedRegex(@"\b(?:that\s+(?:suggestion|basket|option|meal)|the\s+suggested(?:\s+(?:basket|option|meal))?|no\s+allerg(?:y|ies)|dietary\s+requirements?|show\s+(?:me\s+)?(?:alternatives?|cheaper\s+options)|change\s+the\s+basket|I\s+already\s+have|I\s+do\s+not\s+have|I\s+don't\s+have|remove\s+that|remove\s+the\s+unavailable\s+item|leave\s+that\s+out|use\s+that\s+instead)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PriorProposalReference();

    [GeneratedRegex(@"^(?:my\s+(?:maximum\s+)?budget\s+is\s+)?(?:£|GBP\s*)?\s*\d+(?:\.\d{1,2})?\s*(?:pounds?|GBP)?[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex BudgetOnlyAnswer();
}
