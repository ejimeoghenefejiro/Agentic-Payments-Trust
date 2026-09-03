namespace AgentTrust.Mandates;

public interface IMandateStore
{
    void Save(FinancialMandate mandate);
    FinancialMandate? Find(string mandateId);
    FinancialMandate? FindVersion(string mandateId, int version);
    IReadOnlyList<FinancialMandate> GetHistory(string mandateId);
    IReadOnlyList<FinancialMandate> FindByAgent(string agentId);
    IReadOnlyList<FinancialMandate> FindByPrincipal(string principalId);
}

public sealed class InMemoryMandateStore : IMandateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, int Version), FinancialMandate> _mandates = new();

    public void Save(FinancialMandate mandate)
    {
        lock (_gate)
        {
            var active = _mandates.Values.FirstOrDefault(m => m.MandateId == mandate.MandateId && m.Status == MandateStatus.Active);
            if (active is not null && mandate.Version > active.Version)
                _mandates[(active.MandateId, active.Version)] = active with { Status = MandateStatus.Superseded };
            _mandates[(mandate.MandateId, mandate.Version)] = mandate;
        }
    }

    public FinancialMandate? Find(string mandateId)
    { lock (_gate) return _mandates.Values.Where(m => m.MandateId == mandateId).OrderByDescending(m => m.Version).FirstOrDefault(); }

    public FinancialMandate? FindVersion(string mandateId, int version)
    { lock (_gate) return _mandates.GetValueOrDefault((mandateId, version)); }

    public IReadOnlyList<FinancialMandate> GetHistory(string mandateId)
    { lock (_gate) return _mandates.Values.Where(m => m.MandateId == mandateId).OrderBy(m => m.Version).ToList(); }

    public IReadOnlyList<FinancialMandate> FindByAgent(string agentId)
    { lock (_gate) return _mandates.Values.Where(m => m.AgentId == agentId).ToList(); }
    public IReadOnlyList<FinancialMandate> FindByPrincipal(string principalId)
    { lock (_gate) return _mandates.Values.Where(m => m.PrincipalId == principalId).GroupBy(m => m.MandateId).Select(g => g.OrderByDescending(m => m.Version).First()).ToList(); }
}
