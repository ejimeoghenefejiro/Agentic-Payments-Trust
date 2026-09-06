namespace AgentTrust.Mandates;

public enum MandateLimitChangeStatus { AwaitingStepUp, Applied, Expired, Cancelled }
public sealed record MandateLimitChangeProposal(string ProposalId,string MandateId,int BaseMandateVersion,
    string PrincipalId,decimal? PerTransactionLimit,decimal? WeeklyLimit,decimal? MonthlyLimit,string Currency,
    MandateLimitChangeStatus Status,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,string RequestedThrough,
    DateTimeOffset? AppliedAt=null,string? AppliedBy=null,long Version=1);

public interface IMandateLimitChangeStore
{
    void Save(MandateLimitChangeProposal proposal);
    MandateLimitChangeProposal? FindOwned(string proposalId,string principalId);
    bool TryApply(string proposalId,string principalId,DateTimeOffset now,out FinancialMandate? mandate,out IReadOnlyList<string> reasons);
}

public sealed class InMemoryMandateLimitChangeStore:IMandateLimitChangeStore
{
    private readonly object _gate=new();private readonly Dictionary<string,MandateLimitChangeProposal> _items=[];private readonly IMandateStore _mandates;
    public InMemoryMandateLimitChangeStore(IMandateStore mandates)=>_mandates=mandates;
    public void Save(MandateLimitChangeProposal proposal){lock(_gate)_items[proposal.ProposalId]=proposal;}
    public MandateLimitChangeProposal? FindOwned(string id,string principal){lock(_gate)return _items.GetValueOrDefault(id)is{} x&&x.PrincipalId==principal?x:null;}
    public bool TryApply(string id,string principal,DateTimeOffset now,out FinancialMandate? mandate,out IReadOnlyList<string> reasons)
    {
        lock(_gate){var p=FindOwned(id,principal);if(p is null){mandate=null;reasons=["PROPOSAL_NOT_FOUND"];return false;}var current=_mandates.Find(p.MandateId);
            if(p.Status!=MandateLimitChangeStatus.AwaitingStepUp){mandate=null;reasons=["PROPOSAL_NOT_PENDING"];return false;}if(p.ExpiresAt<now){_items[id]=p with{Status=MandateLimitChangeStatus.Expired,Version=p.Version+1};mandate=null;reasons=["PROPOSAL_EXPIRED"];return false;}
            if(current is null||current.PrincipalId!=principal||current.Version!=p.BaseMandateVersion||!current.IsActive(now)){mandate=null;reasons=["MANDATE_CHANGED_OR_INACTIVE"];return false;}
            mandate=current with{PerTransactionLimit=p.PerTransactionLimit??current.PerTransactionLimit,WeeklyLimit=p.WeeklyLimit??current.WeeklyLimit,MonthlyLimit=p.MonthlyLimit??current.MonthlyLimit,Version=current.Version+1,SupersedesMandateId=current.MandateId,CreatedAt=now,EffectiveFrom=now};_mandates.Save(mandate);_items[id]=p with{Status=MandateLimitChangeStatus.Applied,AppliedAt=now,AppliedBy=principal,Version=p.Version+1};reasons=[];return true;}
    }
}

public sealed class MandateLimitChangeService
{
    private readonly IMandateStore _mandates;private readonly IMandateLimitChangeStore _proposals;
    public MandateLimitChangeService(IMandateStore mandates,IMandateLimitChangeStore proposals){_mandates=mandates;_proposals=proposals;}
    public MandateLimitChangeProposal Propose(string mandateId,string principal,decimal? perTransaction,decimal? weekly,decimal? monthly,DateTimeOffset now,string channel="chat")
    {
        var mandate=_mandates.Find(mandateId)??throw new KeyNotFoundException("Mandate not found.");if(mandate.PrincipalId!=principal)throw new UnauthorizedAccessException();if(!mandate.IsActive(now))throw new InvalidOperationException("Only an active, unexpired mandate can be changed.");
        if(perTransaction is null&&weekly is null&&monthly is null)throw new ArgumentException("At least one limit is required.");if(new[]{perTransaction,weekly,monthly}.Any(x=>x is<=0))throw new ArgumentOutOfRangeException(nameof(perTransaction),"Limits must be positive.");
        var effectivePerTransaction=perTransaction??mandate.PerTransactionLimit;var effectiveWeekly=weekly??mandate.WeeklyLimit;var effectiveMonthly=monthly??mandate.MonthlyLimit;
        if(effectiveWeekly is not null&&effectiveWeekly<effectivePerTransaction)throw new ArgumentException("Weekly limit cannot be lower than the per-transaction limit.");if(effectiveMonthly is not null&&effectiveWeekly is not null&&effectiveMonthly<effectiveWeekly)throw new ArgumentException("Monthly limit cannot be lower than the weekly limit.");
        var proposal=new MandateLimitChangeProposal($"mlcp_{Guid.NewGuid():N}",mandate.MandateId,mandate.Version,principal,perTransaction,weekly,monthly,mandate.Currency,MandateLimitChangeStatus.AwaitingStepUp,now,now.AddMinutes(15),channel);_proposals.Save(proposal);return proposal;
    }
}
