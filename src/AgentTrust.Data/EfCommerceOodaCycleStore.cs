using AgentTrust.Commerce;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

public sealed class EfCommerceOodaCycleStore(AgentTrustDbContext db) : ICommerceOodaCycleStore
{
    public CommerceOodaCycle? FindOwned(string purchaseIntentId, string principalId)
    {
        var row = db.CommerceOodaCycles.AsNoTracking()
            .SingleOrDefault(candidate => candidate.PurchaseIntentId == purchaseIntentId
                && candidate.PrincipalId == principalId);
        return row is null ? null : Map(row);
    }

    public void Save(CommerceOodaCycle cycle)
    {
        var row = db.CommerceOodaCycles.SingleOrDefault(candidate => candidate.CycleId == cycle.CycleId);
        if (row is null)
        {
            row = new CommerceOodaCycleEntity { CycleId = cycle.CycleId };
            db.CommerceOodaCycles.Add(row);
        }

        row.TaskId = cycle.TaskId;
        row.PrincipalId = cycle.PrincipalId;
        row.PurchaseIntentId = cycle.PurchaseIntentId;
        row.ScheduledFor = cycle.ScheduledFor;
        row.CycleNumber = cycle.CycleNumber;
        row.Status = cycle.Status.ToString();
        row.GoalJson = cycle.GoalJson;
        row.ObservationsJson = cycle.ObservationsJson;
        row.AlternativesJson = cycle.AlternativesJson;
        row.DecisionJson = cycle.DecisionJson;
        row.ActionJson = cycle.ActionJson;
        row.ProofJson = cycle.ProofJson;
        row.Outcome = cycle.Outcome;
        row.CreatedAt = cycle.CreatedAt;
        row.UpdatedAt = cycle.UpdatedAt;
        row.Version = cycle.Version;
        db.SaveChanges();
    }

    public IReadOnlyList<CommerceOodaCycle> FindByTaskOwned(string taskId, string principalId) =>
        db.CommerceOodaCycles.AsNoTracking()
            .Where(candidate => candidate.TaskId == taskId && candidate.PrincipalId == principalId)
            .OrderByDescending(candidate => candidate.ScheduledFor)
            .AsEnumerable().Select(Map).ToArray();

    public IReadOnlyList<CommerceOodaStep> StepsOwned(string cycleId,string principalId)=>
        db.CommerceOodaSteps.AsNoTracking().Where(x=>x.CycleId==cycleId&&x.PrincipalId==principalId)
            .OrderBy(x=>x.CycleNumber).ThenBy(x=>x.Sequence).AsEnumerable().Select(MapStep).ToArray();

    public void Append(CommerceOodaStep step)
    {
        if(db.CommerceOodaSteps.Any(x=>x.StepId==step.StepId))return;
        db.CommerceOodaSteps.Add(new(){StepId=step.StepId,CycleId=step.CycleId,PrincipalId=step.PrincipalId,
            CycleNumber=step.CycleNumber,Sequence=step.Sequence,Phase=step.Phase.ToString(),InputJson=step.InputJson,
            OutputJson=step.OutputJson,EvidenceJson=step.EvidenceJson,DecisionReason=step.DecisionReason,CreatedAt=step.CreatedAt});
        db.SaveChanges();
    }

    private static CommerceOodaCycle Map(CommerceOodaCycleEntity row) => new(
        row.CycleId, row.TaskId, row.PrincipalId, row.PurchaseIntentId, row.ScheduledFor,
        row.CycleNumber, Enum.Parse<CommerceOodaStatus>(row.Status), row.GoalJson,
        row.ObservationsJson, row.AlternativesJson, row.DecisionJson, row.ActionJson,
        row.ProofJson, row.Outcome, row.CreatedAt, row.UpdatedAt, row.Version);
    private static CommerceOodaStep MapStep(CommerceOodaStepEntity row)=>new(row.StepId,row.CycleId,row.PrincipalId,
        row.CycleNumber,row.Sequence,Enum.Parse<CommerceOodaStatus>(row.Phase),row.InputJson,row.OutputJson,
        row.EvidenceJson,row.DecisionReason,row.CreatedAt);
}
