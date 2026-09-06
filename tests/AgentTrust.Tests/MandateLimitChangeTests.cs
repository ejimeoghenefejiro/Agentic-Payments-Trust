using AgentTrust.Mandates;

namespace AgentTrust.Tests;

public sealed class MandateLimitChangeTests
{
    private static readonly DateTimeOffset Now=new(2026,9,5,12,0,0,TimeSpan.Zero);
    [Fact]
    public void ConfirmationCreatesNewVersionAndPreservesOriginal()
    {
        var mandates=new InMemoryMandateStore();var original=Mandate();mandates.Save(original);var proposals=new InMemoryMandateLimitChangeStore(mandates);var service=new MandateLimitChangeService(mandates,proposals);
        var proposal=service.Propose(original.MandateId,"principal-1",null,100m,null,Now);
        Assert.Equal(MandateLimitChangeStatus.AwaitingStepUp,proposal.Status);Assert.True(proposals.TryApply(proposal.ProposalId,"principal-1",Now.AddMinutes(1),out var updated,out var reasons));
        Assert.Empty(reasons);Assert.Equal(2,updated!.Version);Assert.Equal(100m,updated.WeeklyLimit);Assert.Equal(25m,updated.PerTransactionLimit);Assert.Equal(MandateStatus.Superseded,mandates.FindVersion(original.MandateId,1)!.Status);Assert.Equal(MandateStatus.Active,mandates.FindVersion(original.MandateId,2)!.Status);
    }
    [Fact]
    public void AnotherPrincipalCannotReadOrApplyProposal()
    {
        var mandates=new InMemoryMandateStore();var original=Mandate();mandates.Save(original);var proposals=new InMemoryMandateLimitChangeStore(mandates);var proposal=new MandateLimitChangeService(mandates,proposals).Propose(original.MandateId,"principal-1",null,100m,null,Now);
        Assert.Null(proposals.FindOwned(proposal.ProposalId,"principal-2"));Assert.False(proposals.TryApply(proposal.ProposalId,"principal-2",Now,out _,out var reasons));Assert.Contains("PROPOSAL_NOT_FOUND",reasons);Assert.Equal(1,mandates.Find(original.MandateId)!.Version);
    }
    [Fact]
    public void ExpiredOrStaleProposalCannotChangeAuthority()
    {
        var mandates=new InMemoryMandateStore();var original=Mandate();mandates.Save(original);var proposals=new InMemoryMandateLimitChangeStore(mandates);var service=new MandateLimitChangeService(mandates,proposals);var expired=service.Propose(original.MandateId,"principal-1",null,100m,null,Now);
        Assert.False(proposals.TryApply(expired.ProposalId,"principal-1",Now.AddMinutes(16),out _,out var reasons));Assert.Contains("PROPOSAL_EXPIRED",reasons);Assert.Equal(1,mandates.Find(original.MandateId)!.Version);
    }
    private static FinancialMandate Mandate()=>new("mandate-1","principal-1","agent-1","GroceryDemo","groceries","pm-1",25m,50m,null,"GBP",new Dictionary<string,string>(),AboveLimitAction.RequireApproval,MandateStatus.Active,Now.AddDays(-1),Now.AddYears(1));
}
