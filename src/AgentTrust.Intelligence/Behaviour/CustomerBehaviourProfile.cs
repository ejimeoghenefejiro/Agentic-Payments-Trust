namespace AgentTrust.Intelligence.Behaviour;

/// <summary>
/// A customer's learned normal envelope, e.g. the doc's example: "Normal transaction £30-£400,
/// normal locations Manchester/Salford, typical devices D44/D71, typical merchants M14/M18/M33,
/// regular beneficiaries B101/B201, typical time 07:00-23:00." Rebuilt from history — this is
/// deliberately a snapshot, not a permanently fixed model, matching the doc's requirement that
/// behaviour "should also change over time."
/// </summary>
public sealed record CustomerBehaviourProfile(
    string CustomerId,
    decimal TypicalMinAmount,
    decimal TypicalMaxAmount,
    IReadOnlyList<string> TypicalLocations,
    IReadOnlyList<string> TypicalDevices,
    IReadOnlyList<string> TypicalMerchants,
    IReadOnlyList<string> RegularBeneficiaries,
    TimeOnly TypicalWindowStart,
    TimeOnly TypicalWindowEnd,
    int SampleSize)
{
    public bool IsKnownLocation(string location) => TypicalLocations.Contains(location, StringComparer.OrdinalIgnoreCase);
    public bool IsKnownDevice(string deviceId) => TypicalDevices.Contains(deviceId, StringComparer.OrdinalIgnoreCase);
    public bool IsKnownBeneficiary(string? beneficiaryId) =>
        beneficiaryId is not null && RegularBeneficiaries.Contains(beneficiaryId, StringComparer.OrdinalIgnoreCase);

    public bool IsWithinTypicalWindow(TimeOnly time)
    {
        if (TypicalWindowStart <= TypicalWindowEnd)
        {
            return time >= TypicalWindowStart && time <= TypicalWindowEnd;
        }
        // window wraps midnight
        return time >= TypicalWindowStart || time <= TypicalWindowEnd;
    }

    public bool IsWithinTypicalAmount(decimal amount) => amount >= TypicalMinAmount && amount <= TypicalMaxAmount;
}
