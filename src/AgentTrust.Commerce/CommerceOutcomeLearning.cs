using AgentTrust.Consumer;

namespace AgentTrust.Commerce;

public sealed record VerifiedCommerceOutcome(string PrincipalId,string PurchaseIntentId,string MerchantId,
    IReadOnlyList<BasketItem> Items,IReadOnlyList<CommerceGoalProof> GoalProofs,
    string ReceiptId,string FulfilmentId,FulfilmentStatus FulfilmentStatus);

/// <summary>
/// Learns only from outcomes carrying payment, receipt and fulfilment evidence.
/// Every learned entry retains provenance and can be individually rolled back.
/// </summary>
public sealed class CommerceOutcomeLearningService(IConsumerMemoryService memory)
{
    public IReadOnlyList<string> Learn(VerifiedCommerceOutcome outcome)
    {
        if(string.IsNullOrWhiteSpace(outcome.ReceiptId)||string.IsNullOrWhiteSpace(outcome.FulfilmentId))
            throw new InvalidOperationException("VERIFIED_OUTCOME_EVIDENCE_REQUIRED");
        if(outcome.FulfilmentStatus is FulfilmentStatus.Failed or FulfilmentStatus.Cancelled or FulfilmentStatus.Unknown)
            throw new InvalidOperationException("SUCCESSFUL_FULFILMENT_EVIDENCE_REQUIRED");

        var expires=DateTimeOffset.UtcNow.AddDays(90);
        var ids=new List<string>();
        foreach(var item in outcome.Items)
        {
            var substituted=outcome.GoalProofs.Any(proof=>proof.ProductId==item.ProductId&&proof.Substituted);
            var entry=memory.Remember(outcome.PrincipalId,
                substituted?ConsumerMemoryKind.Substitution:ConsumerMemoryKind.Preference,
                ConsumerMemoryPolarity.Positive,item.ProductId,$"Verified accepted item {item.Description}",
                "verified-payment-fulfilment-receipt",purchaseIntentId:outcome.PurchaseIntentId,
                confidence:substituted ? .95 : .85,expiresAt:expires);
            ids.Add(entry.MemoryId);
        }
        ids.Add(memory.Remember(outcome.PrincipalId,ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,
            outcome.MerchantId,$"Verified successful provider {outcome.MerchantId}",
            "verified-payment-fulfilment-receipt",purchaseIntentId:outcome.PurchaseIntentId,
            confidence:.8,expiresAt:expires).MemoryId);
        return ids;
    }

    public ConsumerMemoryEntry Reject(string principalId,string productId,string reason,string? purchaseIntentId=null)=>
        memory.Remember(principalId,ConsumerMemoryKind.Correction,ConsumerMemoryPolarity.Negative,productId,reason,
            "verified-customer-correction",purchaseIntentId:purchaseIntentId,confidence:1);

    public int Rollback(string principalId,IEnumerable<string> memoryIds)=>
        memoryIds.Distinct(StringComparer.Ordinal).Count(id=>memory.Delete(principalId,id));
}
