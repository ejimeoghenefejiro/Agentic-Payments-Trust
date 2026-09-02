namespace AgentTrust.Mandates;

public interface IMandateStore
{
    void Save(FinancialMandate mandate);
    FinancialMandate? Find(string mandateId);
    IReadOnlyList<FinancialMandate> FindByAgent(string agentId);
}

public sealed class InMemoryMandateStore : IMandateStore
{
    private readonly Dictionary<string, FinancialMandate> _mandates = new();

    public void Save(FinancialMandate mandate) => _mandates[mandate.MandateId] = mandate;

    public FinancialMandate? Find(string mandateId) => _mandates.GetValueOrDefault(mandateId);

    public IReadOnlyList<FinancialMandate> FindByAgent(string agentId) =>
        _mandates.Values.Where(m => m.AgentId == agentId).ToList();
}
