namespace AgentTrust.Intelligence.Behaviour;

/// <summary>
/// A merchant's learned normal envelope, e.g. the doc's example: "150 transactions/day, average
/// £22, refund rate 2%, mostly UK customers." A material shift (4,300 tx/day, £480 average, 18%
/// refunds) is what MerchantBehaviourProfileBuilder.HasMaterialShift flags for investigation.
/// </summary>
public sealed record MerchantBehaviourProfile(
    string MerchantId,
    double AverageDailyTransactionCount,
    decimal AverageTransactionAmount,
    double RefundRate,
    IReadOnlyList<string> TypicalCustomerLocations,
    int SampleSize);
