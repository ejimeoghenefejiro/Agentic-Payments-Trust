using System.Text.RegularExpressions;
using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Investigation;

public static partial class InvestigationSecurityPolicy
{
    public const int MaxModelResponseCharacters = 32_768;
    public const int MaxToolArgumentCharacters = 512;
    public const int MaxRationaleCharacters = 4_000;
    public const int MaxHypotheses = 12;
    public const int MaxEvidenceItemsPerHypothesis = 50;
    public const int MaxOpenQuestions = 30;

    public static void ValidateCandidate(TransactionEvent candidate)
    {
        ValidateIdentifier(candidate.TransactionId, nameof(candidate.TransactionId));
        ValidateIdentifier(candidate.CustomerId, nameof(candidate.CustomerId));
        ValidateIdentifier(candidate.MerchantId, nameof(candidate.MerchantId));
        ValidateIdentifier(candidate.DeviceId, nameof(candidate.DeviceId));
        if (candidate.BeneficiaryId is not null) ValidateIdentifier(candidate.BeneficiaryId, nameof(candidate.BeneficiaryId));
        if (candidate.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(candidate.Amount), "Transaction amount must be positive.");
        if (candidate.Currency.Length != 3 || !candidate.Currency.All(char.IsLetter))
            throw new ArgumentException("Currency must be a three-letter code.", nameof(candidate.Currency));
    }

    public static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !SafeIdentifier().IsMatch(value))
            throw new ArgumentException($"{name} contains invalid characters or length.", name);
    }

    public static void ValidateArguments(IReadOnlyDictionary<string, string> arguments)
    {
        if (arguments.Count > 8) throw new InvalidOperationException("Too many tool arguments.");
        foreach (var (key, value) in arguments)
        {
            if (key.Length > 64 || value.Length > MaxToolArgumentCharacters || !SafeArgumentName().IsMatch(key))
                throw new InvalidOperationException("Tool argument violates the investigation security policy.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._:@/-]+$")]
    private static partial Regex SafeIdentifier();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex SafeArgumentName();
}
