namespace AgentTrust.Commerce;

public interface ICommerceDurability
{
    void SaveIntent(PurchaseIntent intent, string executionId, int mandateVersion);
    void SaveAuthorisation(PurchaseAuthorisation authorisation);
    void SaveCheckout(PurchaseIntent intent, string status);
    void SaveReceipt(PurchaseReceipt receipt, string principalId);
    PurchaseReceipt? FindReceiptOwned(string receiptId, string principalId);
}

public sealed class NullCommerceDurability : ICommerceDurability
{
    public void SaveIntent(PurchaseIntent intent, string executionId, int mandateVersion) { }
    public void SaveAuthorisation(PurchaseAuthorisation authorisation) { }
    public void SaveCheckout(PurchaseIntent intent, string status) { }
    public void SaveReceipt(PurchaseReceipt receipt, string principalId) { }
    public PurchaseReceipt? FindReceiptOwned(string receiptId, string principalId) => null;
}
