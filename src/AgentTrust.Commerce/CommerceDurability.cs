namespace AgentTrust.Commerce;

public sealed record DurablePendingPurchase(PurchaseIntent Intent,string Fingerprint,string ReservationId,int MandateVersion);

public interface ICommerceDurability
{
    void SaveIntent(PurchaseIntent intent, string executionId, int mandateVersion);
    void SaveAuthorisation(PurchaseAuthorisation authorisation);
    void SaveCheckout(PurchaseIntent intent, string status);
    void BeginPaymentSubmission(PurchaseIntent intent, string provider);
    void RecordPaymentResult(PurchaseIntent intent, PlatformPaymentResult result);
    void RecordPaymentUnknown(PurchaseIntent intent, string? failureCode);
    void SaveReceipt(PurchaseReceipt receipt, string principalId);
    PurchaseReceipt? FindReceiptOwned(string receiptId, string principalId);
    PurchaseReceipt? FindReceiptByPurchaseOwned(string purchaseIntentId, string principalId);
    void SavePending(PurchaseIntent intent,string transactionFingerprint,string reservationId,int mandateVersion);
    DurablePendingPurchase? FindPendingOwned(string purchaseIntentId,string principalId);
    void CompletePending(string purchaseIntentId,bool approved,string approver);
    PurchaseIntent? FindIntentOwned(string purchaseIntentId,string principalId);
}

public sealed class NullCommerceDurability : ICommerceDurability
{
    public void SaveIntent(PurchaseIntent intent, string executionId, int mandateVersion) { }
    public void SaveAuthorisation(PurchaseAuthorisation authorisation) { }
    public void SaveCheckout(PurchaseIntent intent, string status) { }
    public void BeginPaymentSubmission(PurchaseIntent intent, string provider) { }
    public void RecordPaymentResult(PurchaseIntent intent, PlatformPaymentResult result) { }
    public void RecordPaymentUnknown(PurchaseIntent intent, string? failureCode) { }
    public void SaveReceipt(PurchaseReceipt receipt, string principalId) { }
    public PurchaseReceipt? FindReceiptOwned(string receiptId, string principalId) => null;
    public PurchaseReceipt? FindReceiptByPurchaseOwned(string purchaseIntentId, string principalId) => null;
    public void SavePending(PurchaseIntent intent,string transactionFingerprint,string reservationId,int mandateVersion){}
    public DurablePendingPurchase? FindPendingOwned(string purchaseIntentId,string principalId)=>null;
    public void CompletePending(string purchaseIntentId,bool approved,string approver){}
    public PurchaseIntent? FindIntentOwned(string purchaseIntentId,string principalId)=>null;
}

public sealed class InMemoryCommerceDurability : ICommerceDurability
{
    private readonly object _gate=new();private readonly Dictionary<string,PurchaseReceipt> _receipts=new();
    private readonly Dictionary<string,string> _owners=new();private readonly Dictionary<string,PurchaseIntent> _intents=new();private readonly Dictionary<string,DurablePendingPurchase> _pending=new();
    private readonly Dictionary<string,(string Status,int Submissions,string? ProviderPaymentId)> _payments=new();
    public void SaveIntent(PurchaseIntent intent,string executionId,int mandateVersion){lock(_gate){_owners[intent.PurchaseIntentId]=intent.PrincipalId;_intents[intent.PurchaseIntentId]=intent;}}
    public void SaveAuthorisation(PurchaseAuthorisation authorisation){}
    public void SaveCheckout(PurchaseIntent intent,string status){}
    public void BeginPaymentSubmission(PurchaseIntent intent,string provider){lock(_gate){var current=_payments.GetValueOrDefault(intent.IdempotencyKey);_payments[intent.IdempotencyKey]=(current.Status??"Submitted",current.Submissions+1,current.ProviderPaymentId);}}
    public void RecordPaymentResult(PurchaseIntent intent,PlatformPaymentResult result){lock(_gate)_payments[intent.IdempotencyKey]=(result.Status.ToString(),Math.Max(1,_payments.GetValueOrDefault(intent.IdempotencyKey).Submissions),result.ProviderReference);}
    public void RecordPaymentUnknown(PurchaseIntent intent,string? failureCode){lock(_gate){var current=_payments.GetValueOrDefault(intent.IdempotencyKey);_payments[intent.IdempotencyKey]=(PlatformPaymentStatus.Unknown.ToString(),Math.Max(1,current.Submissions),current.ProviderPaymentId);}}
    public void SaveReceipt(PurchaseReceipt receipt,string principalId){lock(_gate){if(_receipts.Values.Any(x=>x.PurchaseIntentId==receipt.PurchaseIntentId))return;_receipts.TryAdd(receipt.ReceiptId,receipt);_owners[receipt.PurchaseIntentId]=principalId;}}
    public PurchaseReceipt? FindReceiptOwned(string id,string principal){lock(_gate)return _receipts.GetValueOrDefault(id)is{} r&&_owners.GetValueOrDefault(r.PurchaseIntentId)==principal?r:null;}
    public PurchaseReceipt? FindReceiptByPurchaseOwned(string id,string principal){lock(_gate)return _owners.GetValueOrDefault(id)==principal?_receipts.Values.FirstOrDefault(x=>x.PurchaseIntentId==id):null;}
    public void SavePending(PurchaseIntent intent,string transactionFingerprint,string reservationId,int mandateVersion){lock(_gate)_pending[intent.PurchaseIntentId]=new(intent,transactionFingerprint,reservationId,mandateVersion);}
    public DurablePendingPurchase? FindPendingOwned(string id,string principal){lock(_gate)return _pending.GetValueOrDefault(id)is{} p&&p.Intent.PrincipalId==principal?p:null;}
    public void CompletePending(string purchaseIntentId,bool approved,string approver){lock(_gate)_pending.Remove(purchaseIntentId);}
    public PurchaseIntent? FindIntentOwned(string id,string principal){lock(_gate)return _owners.GetValueOrDefault(id)==principal?_intents.GetValueOrDefault(id):null;}
}
